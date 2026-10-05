#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""装机前预检：确认 TypeBridge.dll 与目标游戏匹配。

    python preflight.py [--game 游戏目录] [--bridge TypeBridge.dll] [--map bridge_map.tsv]

查映射表是否命中目标 DLL，以及 DLL 绑定的 Mono.Cecil API 形态是否与目标一致
（0.10.4 是 Collection<TypeReference>，0.11.6 是 Collection<GenericParameterConstraint>，
 不一致会在运行时抛 MissingMethodException，编译期看不出来）。
"""
import argparse
import re
import subprocess
import sys
from pathlib import Path

try:
    sys.stdout.reconfigure(encoding="utf-8")
except Exception:
    pass

ILSPY = r"C:/Users/afs/.dotnet/tools/ilspycmd.exe"


def cecil_version(path: Path):
    """从 dll 字节里捞 Mono.Cecil 的版本号（非强命名，够用）。"""
    b = path.read_bytes()
    m = re.findall(rb"0\.1[01]\.\d+\.\d+", b)
    return sorted({x.decode() for x in m})


def cecil_api_flavour(path: Path):
    """判断该 Cecil 的 Constraints 返回哪个类型。"""
    b = path.read_bytes()
    has_gpc = b"GenericParameterConstraint" in b
    return "GenericParameterConstraint" if has_gpc else "TypeReference"


def bridge_api_flavour(path: Path):
    """从 TypeBridge.dll 的 IL 看它调用的是哪个 Constraints 形态。"""
    try:
        il = subprocess.run([ILSPY, "-il", str(path)], capture_output=True,
                            text=True, encoding="utf-8", errors="replace").stdout
    except Exception as e:
        return "查询失败: %s" % e
    if "GenericParameterConstraint" in il and "get_ConstraintType" in il:
        return "GenericParameterConstraint"
    if re.search(r"Collection`1<class \[Mono\.Cecil\]Mono\.Cecil\.TypeReference>\s*\[Mono\.Cecil\]Mono\.Cecil\.GenericParameter::get_Constraints", il):
        return "TypeReference"
    return "未知"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--game", default=r"D:/zm/新建文件夹 (3)/1.7 内测 10.2")
    ap.add_argument("--bridge", default=r"D:/zm/新建文件夹 (3)/_typebridge/TypeBridge.fixed.dll")
    ap.add_argument("--map", default=r"D:/zm/新建文件夹 (3)/_obfmap/bridge_tables/bridge_map.tsv")
    a = ap.parse_args()

    game = Path(a.game)
    managed = game / "Spaceflight Simulator_Data" / "Managed"
    target_dll = managed / "Assembly-CSharp.dll"
    cecil = managed / "Mono.Cecil.dll"
    bridge = Path(a.bridge)
    tsv = Path(a.map)

    print("=" * 72)
    print("TypeBridge 装机预检")
    print("=" * 72)
    fail = 0

    # ---- 0) 基本存在性 ----
    for lbl, p in [("目标 Assembly-CSharp.dll", target_dll), ("目标 Mono.Cecil.dll", cecil),
                   ("待装 TypeBridge.dll", bridge), ("映射表 bridge_map.tsv", tsv)]:
        ok = p.exists()
        print("[0] %-26s %s" % (lbl, "✓ %s" % (("%d 字节" % p.stat().st_size) if ok else "") if ok else "✗ 不存在"))
        if not ok:
            fail += 1
    if fail:
        print("\n关键文件缺失，停止。")
        return 1

    raw = target_dll.read_bytes()
    is_obf = b"__BB_OBFUSCATOR" in raw
    print("[1] 目标是否混淆版: %s" % ("是 ✓（需要桥）" if is_obf else "否 ✗（不需桥，装了也白装）"))
    if not is_obf:
        fail += 1

    # ---- 2) 映射表 vs DLL ----
    hit = miss = 0
    missing = []
    for line in tsv.read_text(encoding="utf-8").splitlines():
        if "\t" not in line:
            continue
        real, obf = line.split("\t")[:2]
        # ⚠️ 末段要**同时按 '.' 和 '+' 切**：嵌套类型是 Outer+Inner 形式
        tail = obf.rsplit(".", 1)[-1].rsplit("+", 1)[-1]
        if tail and tail.encode("ascii", "ignore") and tail.encode() in raw:
            hit += 1
        else:
            miss += 1
            missing.append((real, obf))
    print("[2] 映射表混淆名命中: %d 命中 / %d 缺失" % (hit, miss))
    if miss:
        fail += 1
        for real, obf in missing[:8]:
            print("      缺: %-38s -> %s" % (real[:38], obf))

    # ---- 3) Cecil API 形态比对（最容易翻车的一项）----
    tgt_flavour = cecil_api_flavour(cecil)
    bdg_flavour = bridge_api_flavour(bridge)
    tgt_ver = cecil_version(cecil)
    print("[3] 目标 Mono.Cecil 版本: %s，接口形态: %s" % (tgt_ver, tgt_flavour))
    print("    待装 TypeBridge 绑定的接口形态: %s" % bdg_flavour)
    if bdg_flavour == tgt_flavour:
        print("    ✓ 一致")
    else:
        print("    ✗ 不一致 —— 运行时会 MissingMethodException，所有 mod 改写会失败")
        print("      修法：用**目标游戏那一份** Managed/Mono.Cecil.dll 重新编译")
        fail += 1

    # ---- 4) 安装状态 ----
    inst = managed / "TypeBridge.dll"
    sa = game / "Spaceflight Simulator_Data" / "ScriptingAssemblies.json"
    ri = game / "Spaceflight Simulator_Data" / "RuntimeInitializeOnLoads.json"
    import json
    print("[4] 安装状态")
    print("     Managed/TypeBridge.dll 存在: %s" % inst.exists())
    if sa.exists():
        j = json.loads(sa.read_text(encoding="utf-8"))
        print("     ScriptingAssemblies 含 TypeBridge: %s" % ("TypeBridge.dll" in j.get("names", [])))
    if ri.exists():
        r = json.loads(ri.read_text(encoding="utf-8"))
        print("     RuntimeInitializeOnLoads 含 Install: %s"
              % any(e.get("assemblyName") == "TypeBridge" and e.get("methodName") == "Install"
                    for e in r.get("root", [])))
    # 空 Mods/TypeBridge 目录会让加载器报 FileNotFoundException，属噪音
    stray = game / "Mods" / "TypeBridge"
    if stray.exists():
        empty = not any(stray.iterdir())
        print("     ⚠️ 存在 Mods/TypeBridge 目录%s —— 加载器会尝试读它并报错，建议删除"
              % ("（空）" if empty else "（非空）"))
        if empty:
            fail += 1

    print()
    print("=== %s ===" % ("预检通过，可以启动游戏" if fail == 0 else "有 %d 项问题，先修再跑" % fail))
    return 0 if fail == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())
