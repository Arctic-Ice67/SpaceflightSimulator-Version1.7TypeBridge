# -*- coding: utf-8 -*-
"""盘查 member_map：每个类型的成员是否真存在于目标类型。

用法: python _audit_members.py"""
import csv
import sys
from collections import defaultdict
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
M = Path(r"D:/zm/新建文件夹 (3)/_obfmap")

P_KIND, P_NS, P_NAME, P_TYPE = 0, 2, 3, 4      # T 行用
P_MNAME, P_MTYPE = 5, 4                        # 成员行用


def load_dump(path):
    """返回 {完整类型名: {'members': set(所有成员名), 'byKind': {...}}}

    ⚠️ 必须**同时收 F/M/P 三类**：
      F = 字段、M = 方法、P = 属性
    我第一版只收 F/M，导致 DevSettings 这类**只有属性**的类型全部误报错配
    （DisableAstronauts 等在元数据里是 P 行，其目标名 FAHNOBJLHDE 是属性名，
      而 get_FAHNOBJLHDE 才是方法名）。
    """
    out, cur = defaultdict(lambda: {"members": set(), "fields": [], "methods": []}), None
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        p = line.split("\t")
        if line.startswith("T\t"):
            if len(p) < 5:
                continue
            cur = (p[P_NS] + "." + p[P_NAME]) if p[P_NS] else p[P_NAME]
        elif cur and line[:2] in ("F\t", "M\t", "P\t"):
            if len(p) > P_MNAME:
                out[cur]["members"].add(p[P_MNAME])
                if p[0] == "F":
                    out[cur]["fields"].append((p[P_MNAME], p[P_MTYPE]))
                elif p[0] == "M":
                    out[cur]["methods"].append((p[P_MNAME], p[P_MTYPE]))
    return out


print("加载元数据转储 …")
ref = load_dump(M / "ref_real.tsv")     # 1.6 未混淆
obf = load_dump(M / "new_obf.tsv")      # 1.7 混淆
print("  1.6 类型 %d，1.7 类型 %d" % (len(ref), len(obf)))

# bridge_map 最终值
bmap = {}
for line in (M / "bridge_tables/bridge_map_final.tsv").read_text(encoding="utf-8").splitlines():
    if line.strip():
        p = line.split("\t")
        if len(p) >= 2 and p[0]:
            bmap[p[0]] = p[1]

# 我方 member_map
rows = list(csv.DictReader((M / "bridge_tables/member_map.tsv").open(encoding="utf-8"),
                           delimiter="\t"))
by_type = defaultdict(list)
for r in rows:
    by_type[r["realType"]].append(r)

print("member_map 覆盖 %d 个类型，%d 行" % (len(by_type), len(rows)))
print()

def find_obf(name):
    if name in obf:
        return name
    tail = name.rsplit(".", 1)[-1]
    hits = [k for k in obf if k.rsplit(".", 1)[-1] == tail]
    return hits[0] if len(hits) == 1 else None


stale = []
clean = 0
for real, ent in sorted(by_type.items()):
    obf_name = bmap.get(real)
    if not obf_name:
        continue
    key = find_obf(obf_name)
    if not key:
        stale.append((real, obf_name, None, len(ent), len(ent), "目标类型在 DLL 里找不到"))
        continue
    names = obf[key]["members"]
    bad = [r for r in ent if r["obfMember"] not in names]
    if bad:
        stale.append((real, obf_name, key, len(bad), len(ent), ""))
    else:
        clean += 1

print("=" * 74)
print("完全匹配的类型: %d" % clean)
print("存在错配的类型: %d" % len(stale))
print("=" * 74)
for real, obf_name, key, nb, nt, note in stale[:40]:
    print("❌ %-46s -> %s" % (real[:46], obf_name))
    if note:
        print("     %s" % note)
    else:
        print("     错配 %d/%d 个成员" % (nb, nt))
