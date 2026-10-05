# -*- coding: utf-8 -*-
"""合并两轮判据，输出 bridge_map_final.tsv。

用法: python _merge_arbitration.py"""
import json
import re
import sys
from pathlib import Path

sys.stdout.reconfigure(encoding="utf-8")
MINE = Path(r"D:/zm/新建文件夹 (3)/_obfmap")
OUT = MINE / "bridge_tables"


def load_dump(path):
    types, cur = {}, None
    for line in Path(path).read_text(encoding="utf-8", errors="replace").splitlines():
        p = line.split("\t")
        if line.startswith("T\t"):
            if len(p) < 5:
                continue
            ns, name, kind = p[2], p[3], p[4]
            cur = (ns + "." + name) if ns else name
            types[cur] = {"kind": kind, "member_set": set(), "n": 0}
        elif line[:2] in ("F\t", "M\t", "P\t") and cur:
            if len(p) >= 5:
                types[cur]["member_set"].add(p[4])
                types[cur]["n"] += 1
    return types


ref = load_dump(MINE / "ref_real.tsv")
obf = load_dump(MINE / "new_obf.tsv")


def find(table, name):
    if name in table:
        return name
    tail = name.rsplit(".", 1)[-1]
    hits = [k for k in table if k.rsplit(".", 1)[-1] == tail]
    return hits[0] if len(hits) == 1 else None


conf = json.loads((OUT / "_type_conflicts.json").read_text(encoding="utf-8"))

verdict = {}
detail = {}

for real, d in conf.items():
    rk, ak, bk = find(ref, real), find(obf, d["mine"]), find(obf, d["theirs"])
    if not (rk and ak and bk):
        verdict[real] = d["mine"]; detail[real] = ("默认", "找不到对应类型，保留我方", ""); continue

    # --- 判据 1：成员名集合 ---
    r_named = {m for m in ref[rk]["member_set"] if not re.match(r"^[A-Z][A-Z0-9]{5,}$", m)}
    if r_named:
        a_hit = sum(1 for m in r_named if m in obf[ak]["member_set"])
        b_hit = sum(1 for m in r_named if m in obf[bk]["member_set"])
        if a_hit != b_hit:
            win = d["mine"] if a_hit > b_hit else d["theirs"]
            lvl, why, _ = "确认", "成员名集合比对 %d/%d" % (a_hit, b_hit), ""
            verdict[real] = win
            detail[real] = (lvl, why, "%d:%d" % (a_hit, b_hit))
            continue

    # --- 判据 2：形状指纹 ---
    def shape(k):
        t = obf[k] if k in obf else ref[k]
        return (t["kind"], t["n"], len(t["member_set"]))
    ra, aa, bb = shape(rk), shape(ak), shape(bk)
    sa = sum(1 for i in range(3) if ra[i] == aa[i]) / 3.0
    sb = sum(1 for i in range(3) if ra[i] == bb[i]) / 3.0
    if sa != sb:
        win = d["mine"] if sa > sb else d["theirs"]
        verdict[real] = win
        detail[real] = ("确认", "形状指纹 %.2f vs %.2f" % (sa, sb), "%.2f/%.2f" % (sa, sb))
        continue

    # --- 两者平手 ---
    verdict[real] = d["mine"]
    detail[real] = ("存疑", "两判据均平手，保留我方（已在 low_confidence 中）", "tie")

# ---- 写出 ----
base = {}
for line in (OUT / "bridge_map.tsv").read_text(encoding="utf-8").splitlines():
    if "\t" in line:
        a, b = line.split("\t")[:2]
        base[a] = b

n_conf = n_mine = n_theirs = n_tie = 0
with (OUT / "bridge_map_final.tsv").open("w", encoding="utf-8", newline="") as f:
    for real in sorted(base):
        obf_name = verdict.get(real, base[real])
        if real in conf:
            n_conf += 1
            lvl, why, score = detail.get(real, ("默认", "", ""))
            if obf_name == conf[real]["theirs"] and conf[real]["mine"] != conf[real]["theirs"]:
                n_theirs += 1
            elif obf_name == conf[real]["mine"]:
                n_mine += 1
            if lvl == "存疑":
                n_tie += 1
            f.write("%s\t%s\t%s\t%s\n" % (real, obf_name, lvl, why))
        else:
            f.write("%s\t%s\t沿用\t非冲突项\n" % (real, base[real]))

print("冲突 %d 条裁决结果：" % n_conf)
print("   保留我方 %d / 采纳他方 %d / 平手存疑 %d" % (n_mine, n_theirs, n_tie))
print()
from collections import Counter
c = Counter(d[0] for k, d in detail.items() if k in conf)
print("置信档分布:", dict(c))
print()
print("采纳他方的条目（我方原映射被否）:")
for real, d in conf.items():
    mn, th = d["mine"], d["theirs"]
    if verdict.get(real) == th and mn != th:
        lvl, why, _s = detail.get(real, ("", "", ""))
        print("   %-34s 我=%-22s -> 他=%-22s [%s %s]" % (real[:34], mn[:22], th[:22], lvl, why))
print()
print("已写出 bridge_map_final.tsv")
