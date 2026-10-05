# -*- coding: utf-8 -*-
"""重推 member_map 中指向旧类型的成员（类型名仲裁改名后成员未同步）。

用法: python _rederive_members.py"""
import csv
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
M = Path(r"D:/zm/新建文件夹 (3)/_obfmap")
BT = M / "bridge_tables"

P_NS, P_NAME = 2, 3
P_MNAME, P_MTYPE = 5, 4


def load_dump(path):
    """{类型全名: {'F':[(name,typeFull)], 'M':[...], 'P':[...]}}，保持元数据顺序。"""
    out, cur = defaultdict(lambda: {"F": [], "M": [], "P": []}), None
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        p = line.split("\t")
        if line.startswith("T\t"):
            if len(p) < 5:
                continue
            cur = (p[P_NS] + "." + p[P_NAME]) if p[P_NS] else p[P_NAME]
        elif cur and line[:2] in ("F\t", "M\t", "P\t"):
            if len(p) > P_MNAME:
                out[cur][p[0]].append((p[P_MNAME], p[P_MTYPE]))
    return out


ref = load_dump(M / "ref_real.tsv")
obf = load_dump(M / "new_obf.tsv")


def look(table, name):
    if name in table:
        return name
    tail = name.rsplit(".", 1)[-1]
    hits = [k for k in table if k.rsplit(".", 1)[-1] == tail]
    return hits[0] if len(hits) == 1 else None


# 读入现有 member_map
rows = list(csv.DictReader((BT / "member_map.tsv").open(encoding="utf-8"), delimiter="\t"))
bmap = {}
for line in (BT / "bridge_map_final.tsv").read_text(encoding="utf-8").splitlines():
    if line.strip():
        p = line.split("\t")
        if len(p) >= 2 and p[0]:
            bmap[p[0]] = p[1]

KIND_TO_DUMP = {"field": "F", "method": "M", "prop": "P"}

by_type = defaultdict(list)
for r in rows:
    by_type[r["realType"]].append(r)

print("检查 %d 个类型的成员映射 …" % len(by_type))
fixed_types = []
unfixable = []

for real, ent in sorted(by_type.items()):
    obf_name = bmap.get(real)
    if not obf_name:
        continue
    rk, ok_ = look(ref, real), look(obf, obf_name)
    if not ok_:
        continue
    obf_members = obf[ok_]

    # 找错配行
    valid = set()
    for k in ("F", "M", "P"):
        valid |= {n for n, _ in obf_members[k]}
    bad = [r for r in ent if r["obfMember"] not in valid]
    if not bad:
        continue

    # 按 kind 分组重推
    kinds_bad = defaultdict(list)
    for r in bad:
        kinds_bad[r["kind"]].append(r)

    # 前置条件：参与重推的每个 kind，两边成员数必须完全一致
    ok_counts = True
    plan = {}
    for kind, brs in kinds_bad.items():
        dk = KIND_TO_DUMP[kind]
        rlist = ref[rk][dk]
        olist = obf_members[dk]
        if len(rlist) != len(olist):
            ok_counts = False
            break
        # 逐位对齐：真名 by 顺序 ← 混淆名 by 顺序
        plan[kind] = dict(zip([n for n, _ in rlist], [n for n, _ in olist]))
        # 签名（类型全名）也要能对上 —— 至少应有一半以上位置类型一致
        same_sig = sum(1 for (rn, rt), (on, ot) in zip(rlist, olist) if rt == ot)
        if same_sig < len(rlist) // 2:
            ok_counts = False
            break

    if not ok_counts:
        unfixable.append((real, obf_name, len(bad), len(ent)))
        continue

    # 应用
    changed = 0
    for r in bad:
        kind = r["kind"]
        newname = plan.get(kind, {}).get(r["realMember"])
        if newname and newname != r["obfMember"]:
            r["obfMember"] = newname
            r["basis"] = "rederive-order"
            changed += 1
    fixed_types.append((real, obf_name, len(bad), changed, len(ent)))

print()
print("=" * 76)
print("已重推导 %d 个类型:" % len(fixed_types))
for real, on, nb, nc, nt in fixed_types:
    print("  ✓ %-48s 错配 %d → 修正 %d  (共 %d 成员)" % (real[:48], nb, nc, nt))
if unfixable:
    print()
    print("⚠️ 无法重推导（成员数不等或签名不符），保持原样 %d 个:" % len(unfixable))
    for real, on, nb, nt in unfixable:
        print("  ? %-48s 错配 %d/%d" % (real[:48], nb, nt))

# 写回
if fixed_types:
    with (BT / "member_map.tsv").open("w", encoding="utf-8", newline="") as f:
        f.write("kind\tobfType\tobfMember\trealType\trealMember\tconf\tbasis\tflags\n")
        for r in rows:
            f.write("\t".join([r["kind"], r["obfType"], r["obfMember"], r["realType"],
                               r["realMember"], r["conf"], r["basis"], r["flags"]]) + "\n")
    print()
    print("已写回 member_map.tsv（%d 行）" % len(rows))
else:
    print()
    print("无需改动")
