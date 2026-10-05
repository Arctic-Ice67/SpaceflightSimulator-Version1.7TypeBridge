# -*- coding: utf-8 -*-
"""membermap.json → TypeBridge 的 4 张 tsv。

用法: python _make_bridge_tables.py"""
import argparse
import json
import re
import sys
from collections import Counter, defaultdict
from pathlib import Path

HERE = Path(__file__).resolve().parent
DEFAULT_IN = HERE / "membermap.json"
DEFAULT_OUT = HERE / "bridge_tables"

sys.stdout.reconfigure(encoding="utf-8")

# 看起来像混淆名的判据：Beebyte 生成的都是 10 位以上全大写字母数字
_OBF_RE = re.compile(r"^[A-Z][A-Z0-9]{5,}$")


def is_obf_like(name):
    return bool(_OBF_RE.match(name or ""))


def split_flags(m):
    """把 membermap 里的 M/F/P 块转成对方的 flags 编码。

    对方 flags 取值含义（从 member_map 分布与源码用法反推）：
        S  static
        P  property（该成员是属性访问器）
        I  interface（声明在接口里）
        V  virtual
        VN virtual+newslot
        A  ?
        N  ?
    我方数据没有这些信息的确证，只能给最保守的一组：
        属性块 -> P ；静态 -> S（若能从 overrides 侧信息判断）
    不确定的宁可留空（对方把 '-' 当默认值），也不要瞎标 —— 标错会让
    FixOverrides/FixInterfaces 走错分支，比不标更危险。
    """
    return "-"


def convert(in_path, out_dir):
    data = json.loads(Path(in_path).read_text(encoding="utf-8"))
    types = data.get("types", {})          # 真名 -> 混淆名
    members = data.get("members", {})      # 真类型名 -> {P/M/F: ...}
    paramnames = data.get("paramnames", {})  # 旧类型 -> {"方法(参数)": [[旧],[新]]}
    overrides = data.get("overrides", {})  # 旧类型 -> {"M:方法(参数)": "新名"}
    low_conf = set()
    for item in data.get("low_confidence", []) or []:
        # 结构未文档化，统一转成 str 便于匹配
        low_conf.add(json.dumps(item, ensure_ascii=False, sort_keys=True))

    out_dir = Path(out_dir)
    out_dir.mkdir(parents=True, exist_ok=True)

    # ---------- 1) bridge_map ----------
    # 真名<TAB>混淆名。排除「名字未变」的类型（混淆器没改的没有桥接意义）。
    # ⚠️ 若存在 bridge_map_final.tsv（经 _merge_arbitration.py 裁决过的版本），
    #    优先用它 —— 它对 51 条与对方表冲突的条目做了两轮独立判据裁决：
    #    ①成员名集合比对（比"具体身份"）②形状指纹（比"个数/种类"）。
    final_path = out_dir / "bridge_map_final.tsv"
    n_renamed = 0
    n_arbitrated = 0
    if final_path.exists():
        with (out_dir / "bridge_map.tsv").open("w", encoding="utf-8", newline="") as f:
            for line in final_path.read_text(encoding="utf-8").splitlines():
                if not line.strip():
                    continue
                parts = line.split("\t")
                if len(parts) < 2:
                    continue
                real, obf = parts[0], parts[1]
                lvl = parts[2] if len(parts) > 2 else ""
                if not real or not obf or real == obf:
                    continue
                f.write("%s\t%s\n" % (real, obf))
                n_renamed += 1
                if lvl in ("确认", "存疑"):
                    n_arbitrated += 1
        print("bridge_map.tsv        %6d 行（用裁决版；其中 %d 条经两轮判据仲裁）"
              % (n_renamed, n_arbitrated))
    else:
        with (out_dir / "bridge_map.tsv").open("w", encoding="utf-8", newline="") as f:
            for real, obf in sorted(types.items()):
                if not real or not obf or real == obf:
                    continue
                f.write("%s\t%s\n" % (real, obf))
                n_renamed += 1
        print("bridge_map.tsv        %6d 行（用 membermap 原值，未仲裁）" % n_renamed)

    # 仲裁表要影响成员表：被采纳的混淆名必须同步进 members
    if final_path.exists():
        for line in final_path.read_text(encoding="utf-8").splitlines():
            if not line.strip():
                continue
            parts = line.split("\t")
            if len(parts) >= 2 and parts[0] and parts[1]:
                types[parts[0]] = parts[1]

    # ---------- 2) member_map ----------
    # kind / obfType / obfMember / realType / realMember / conf / basis / flags
    KIND_MAP = {"F": "field", "M": "method", "P": "prop"}
    rows = 0
    field_n = method_n = prop_n = 0
    with (out_dir / "member_map.tsv").open("w", encoding="utf-8", newline="") as f:
        f.write("kind\tobfType\tobfMember\trealType\trealMember\tconf\tbasis\tflags\n")
        for real_type, info in members.items():
            if not isinstance(info, dict):
                continue
            obf_type = types.get(real_type, "")
            if not obf_type or obf_type == real_type:
                # 该类型本身没改名 —— 成员仍可能被改（成员级混淆独立于类型级），
                # 所以 obf_type 退回真名（对方脚本对「混淆名==真名」是容忍的，
                # 他的 LookupMember 会先按 obfType 找，找不到再走 AltTypeName）。
                obf_type = real_type
            for block, kind in KIND_MAP.items():
                tbl = info.get(block) or {}
                if not isinstance(tbl, dict):
                    continue
                for real_member, obf_member in tbl.items():
                    if block == "M":
                        # 我方 M 的键是 "get_X()"/"op_Implicit()" 形态
                        bare = real_member.split("(")[0]
                        if isinstance(obf_member, (list, tuple)):
                            obf_bare = obf_member[0] if obf_member else ""
                        else:
                            obf_bare = obf_member or ""
                        if not obf_bare or obf_bare == bare:
                            continue      # 未改名，无需桥接
                    else:
                        bare = real_member
                        obf_bare = obf_member or ""
                        if not obf_bare or obf_bare == bare:
                            continue
                    f.write("\t".join([
                        kind, obf_type, obf_bare, real_type, bare,
                        "0.90", "membermap", split_flags(kind),
                    ]) + "\n")
                    rows += 1
                    if kind == "field":
                        field_n += 1
                    elif kind == "method":
                        method_n += 1
                    else:
                        prop_n += 1
    print("member_map.tsv        %6d 行（field %d / method %d / prop %d）"
          % (rows, field_n, method_n, prop_n))

    # ---------- 3) member_overrides ----------
    # 人工修正：高置信、必须覆盖自动结果。来源两处：
    #   a) membermap.overrides（沿基类链/接口传播出来的 override 改名）—— 这些是
    #      「模组自己声明的 override 成员名」，正是对方 FixOverrides 需要的。
    #   b) manual_fixes.json（Early_Load/Load 等空方法体的真值）。
    #
    # ⚠️ overrides 的键**没有** "M:" 前缀，实际形态有两种（实测 192 条）：
    #     "Draw()"                                  —— 简单名
    #     "SFS.Variables.I_ObservableMonoBehaviour.get_OnDestroy()"
    #     "set_OnDestroy(System.Action)"            —— 带参数类型
    # 统一处理：取最后一个 '.' 之后的段作为方法名，第一个 '(' 之前作为基名。
    #
    # 另外 manual_fixes.json 的键**有** "M:" 前缀（'M:Early_Load()'），两种都要认。
    ov_rows = []
    for host, table in overrides.items():
        if not isinstance(table, dict):
            continue
        for key, new_name in table.items():
            if not isinstance(key, str) or not isinstance(new_name, str):
                continue
            seg = key.split("(")[0]
            if ":" in seg:                        # manual_fixes 风格 "M:Foo"
                seg = seg.split(":", 1)[1]
            base = seg.split(".")[-1].strip()      # 丢掉 "接口全名." 前缀
            if not base or not new_name or new_name == base:
                continue
            obf_type = types.get(host, host)
            ov_rows.append(("method", obf_type, new_name, host, base))

    # 合并 manual_fixes.json（键带 "M:" 前缀，值为真值）
    mf_path = HERE / "manual_fixes.json"
    if mf_path.exists():
        mf = json.loads(mf_path.read_text(encoding="utf-8"))
        n_mf = 0
        for host, table in mf.items():
            if host.startswith("_") or not isinstance(table, dict):
                continue
            for key, new_name in table.items():
                if not isinstance(key, str) or not isinstance(new_name, str):
                    continue
                seg = key.split("(")[0]
                if ":" in seg:
                    seg = seg.split(":", 1)[1]
                base = seg.split(".")[-1].strip()
                if not base or new_name == base:
                    continue
                obf_type = types.get(host, host)
                ov_rows.append(("method", obf_type, new_name, host, base))
                n_mf += 1
        print("  （含 manual_fixes.json %d 条）" % n_mf)

    # 另外吸收 TypeBridge 原有的 7 条人工修正（实测证据，我方未覆盖）：
    #   SFS.UI.ModGUI.Button 的 _button/_textAdapter/_onClick —— 模组用
    #   AccessTools.FieldRef(typeof(Button), "_button") 按**字符串**反射私有字段，
    #   这类按名反射的目标我方自动对齐拿不到（字符串不是元数据），他实测出来了。
    #   这些 conf 均已由对方标 1.00，作为高置信并入。
    with (out_dir / "member_overrides.tsv").open("w", encoding="utf-8", newline="") as f:
        f.write("kind\tobfType\tobfMember\trealType\trealMember\tconf\tbasis\tflags\n")
        for kind, obf_type, obf_member, real_type, real_member in ov_rows:
            f.write("\t".join([kind, obf_type, obf_member, real_type, real_member,
                                "1.00", "membermap-override", "SP"]) + "\n")

    # 外部并入
    ext_path = Path(r"D:/zm/新建文件夹 (3)/_typebridge/member_overrides.txt")
    n_ext = 0
    if ext_path.exists():
        with (out_dir / "member_overrides.tsv").open("a", encoding="utf-8", newline="") as f:
            for line in ext_path.read_text(encoding="utf-8").splitlines()[1:]:
                if not line.strip():
                    continue
                c = line.split("\t")
                if len(c) < 8:
                    continue
                kind, obf_type, obf_member, real_type, real_member, conf, basis, flags = c[:8]
                # 已在 overrides 里（同一 realType::realMember）则跳过
                if any(r[3] == real_type and r[4] == real_member for r in ov_rows):
                    continue
                f.write("\t".join([kind, obf_type, obf_member, real_type, real_member,
                                    conf, "typebridge-external", flags]) + "\n")
                n_ext += 1
    print("member_overrides.tsv  %6d 行（override 传播 + 人工真值 + 外部并入 %d）"
          % (len(ov_rows) + n_ext, n_ext))

    # ⭐ 优先使用 GenParamMap 的全量表（从 1.6 未混淆 DLL 直接导出，覆盖 3147 个方法）。
    #    paramnames 只覆盖 900 个类型，缺的会让 Harmony 补丁参数改写失败。
    full = out_dir / "param_map_full.tsv"
    if full.exists():
        import shutil as _sh
        _sh.copyfile(full, out_dir / "param_map.tsv")
        n = sum(1 for l in full.read_text(encoding="utf-8").splitlines() if l.strip())
        print("param_map.tsv         %6d 行（用 GenParamMap 全量表）" % n)
        pm = n
    else:
        pm = 0
        with (out_dir / "param_map.tsv").open("w", encoding="utf-8", newline="") as f:
            for old_type, table in paramnames.items():
                if not isinstance(table, dict):
                    continue
                for key, pair in table.items():
                    if not isinstance(pair, (list, tuple)) or len(pair) < 2:
                        continue
                    old_params = list(pair[0]) if pair[0] else []
                    if not old_params:
                        continue
                    m = re.match(r"^([^(]+)\(", key)
                    if not m:
                        continue
                    f.write("%s|%s|%d\t%s\n" % (old_type, m.group(1), len(old_params),
                                                ",".join(old_params)))
                    pm += 1
        print("param_map.tsv         %6d 行（降级：paramnames 格式；对方 2404）" % pm)

    print("\n输出目录: %s" % out_dir)
    for p in sorted(out_dir.glob("*.tsv")):
        print("  %-26s %9d bytes" % (p.name, p.stat().st_size))
    return 0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="inp", default=str(DEFAULT_IN))
    ap.add_argument("--out", dest="out", default=str(DEFAULT_OUT))
    args = ap.parse_args()
    return convert(args.inp, args.out)


if __name__ == "__main__":
    raise SystemExit(main())
