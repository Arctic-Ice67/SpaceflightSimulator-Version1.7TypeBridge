#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""Build the reflection-style full-name map used by the TypeResolve bridge.

  realFull  ->  obfFull      (both in .NET reflection form, nested types use '+')

Namespaces are PRESERVED by Beebyte, so both sides share the namespace of the
top-level declaring type; it is recovered from fp17 (the obfuscated assembly).
"""
import io, os, sys, csv, collections
try:
    sys.stdout.reconfigure(encoding='utf-8')
except Exception:
    pass

HERE = os.path.dirname(os.path.abspath(__file__))

def norm_plus(s):
    return (s or '').strip().replace('/', '+')

def ns_of(full):
    """namespace of a Cecil-style full name (nested uses '/')"""
    if not full:
        return ''
    head = full.split('/')[0]
    i = head.rfind('.')
    return head[:i] if i >= 0 else ''

def build_ns(path):
    """relative-name -> namespace, taken from one assembly's own metadata"""
    d = {}
    for line in io.open(path, encoding='utf-8').read().split('\n')[1:]:
        c = line.split('\t')
        if len(c) < 3:
            continue
        cecil_full, nsp = c[0], c[2]
        p = cecil_full.split('/')
        h = p[0]
        i = h.rfind('.')
        rel = '/'.join(([h[i + 1:]] if i >= 0 else [h]) + p[1:])
        d[rel] = nsp or ''
    return d

def build_full_index(path):
    """relative name -> list of fully-qualified '+'-style names"""
    d = {}
    for line in io.open(path, encoding='utf-8').read().split('\n')[1:]:
        c = line.split('\t')
        if len(c) < 3:
            continue
        p = c[0].split('/')
        head = p[0]
        i = head.rfind('.')
        rel = '+'.join(([head[i + 1:]] if i >= 0 else [head]) + p[1:])
        ns = c[2] or ''
        full = (ns + '.' + rel) if ns else rel
        d.setdefault(rel, []).append(full)
    return d

def build_names(path):
    """set of '+'-style fully-qualified names declared in one assembly"""
    out = set()
    for line in io.open(path, encoding='utf-8').read().split('\n')[1:]:
        c = line.split('\t')
        if len(c) < 3:
            continue
        p = c[0].split('/')
        head = p[0]
        i = head.rfind('.')
        rel = '+'.join(([head[i + 1:]] if i >= 0 else [head]) + p[1:])
        ns = c[2] or ''
        out.add((ns + '.' + rel) if ns else rel)
    return out

def main():
    ns_obf = build_ns(os.path.join(HERE, 'fp17.tsv'))
    # A type that STILL CARRIES ITS REAL NAME in 1.7 maps to itself.  Without this
    # rule the structural matcher happily swaps two similar types
    # (SFS.WorldBase.Difficulty vs SFS.Logs.Difficulty), which then corrupts the
    # declaring-type chain of every nested type under them.
    T17 = build_names(os.path.join(HERE, 'fp17.tsv'))
    IDX16 = build_full_index(os.path.join(HERE, 'fp16.tsv'))
    IDX23 = build_full_index(os.path.join(HERE, 'fp23.tsv'))
    # The mods were compiled against 1.6.00.18, so that is the authority for the
    # REAL names.  Stef moved some types between 1.6.00.18 and 1.6.00.23
    # (e.g. Difficulty lives in SFS.Logs vs SFS.WorldBase), so keying on the wrong
    # build silently misses lookups.
    ns16 = build_ns(os.path.join(HERE, 'fp16.tsv'))
    ns23 = build_ns(os.path.join(HERE, 'fp23.tsv'))
    def ns_real_of(rel, top):
        if rel in ns16: return ns16[rel]
        if top in ns16: return ns16[top]
        if rel in ns23: return ns23[rel]
        return ns23.get(top, '')

    # ------------------------------------------------------------------
    # The structural matcher (match_result.tsv) already carries the resolved
    # real FULL name for each obfuscated type.  That is the only source that
    # can disambiguate two real types sharing a simple name:
    #
    #     SFS.UI.Button        vs  SFS.UI.ModGUI.Button   (both "Button")
    #     LayoutUtility        vs  SFS.UI.LayoutUtility   (both "LayoutUtility")
    #     Utility              vs  SFS.Navigation.Utility (both "Utility")
    #
    # ns16/ns23 below are keyed on the RELATIVE name, so a collision keeps only
    # ONE namespace - both obf types then get the SAME real key and one real
    # name is lost from the map completely.  That is exactly how
    # SFS.UI.IAEQBJKLLGYUYERQ got filed under "SFS.UI.ModGUI.Button" and how
    # SFS.UI.Button / LayoutUtility / Utility vanished: any mod compiled
    # against 1.6 that touches them then fails with
    # "TypeLoadException: Failure has occurred while loading a type."
    # ------------------------------------------------------------------
    AUTH = {}

    def _rel_name(cecil_full):
        """SFS.UI.Foo/Bar -> Foo+Bar (namespace stripped, nested joined with '+')"""
        p = norm_plus(cecil_full).split('+')
        head = p[0]
        i = head.rfind('.')
        return '+'.join(([head[i + 1:]] if i >= 0 else [head]) + p[1:])

    for line in io.open(os.path.join(HERE, 'match_result.tsv'), encoding='utf-8').read().split('\n')[1:]:
        c = line.split('\t')
        if len(c) < 5 or not c[3]:
            continue
        full = norm_plus(c[0])
        real = norm_plus(c[3])
        AUTH[full] = real
        # final_mapping.tsv stores the obf name WITHOUT its namespace, so the
        # relative form must resolve too - keying on the full name alone made
        # every lookup for a namespaced obf type silently miss.
        AUTH.setdefault(_rel_name(full), real)

    def auth_full(obf_p, real_p):
        """authoritative real full name from the matcher, or None"""
        a = AUTH.get(obf_p)
        if not a or '+' in obf_p or '+' in a:
            return None
        return a if a.split('.')[-1] == real_p else None

    rows = list(csv.DictReader(open(os.path.join(HERE, 'FINAL_mapping.tsv'), encoding='utf-8'), delimiter='\t'))
    out = []
    nores = moved = identity_fixed = 0
    for r in rows:
        obf, real = r['混淆名'], r['真名']
        if not real:
            nores += 1
            continue
        obf_p = norm_plus(obf)
        real_p = norm_plus(real)
        top_obf = obf_p.split('+')[0]
        top_real = real_p.split('+')[0]
        # the two sides may sit in DIFFERENT namespaces (Beebyte sometimes moves
        # a type to the global namespace), so look each one up separately
        n_obf = ns_obf.get(obf_p, ns_obf.get(top_obf, ''))
        n_real = ns_real_of(real_p, top_real)
        real_full = (n_real + '.' + real_p) if n_real else real_p
        # Namespace lookups keyed on the RELATIVE name are ambiguous when two
        # types share a simple name (SFS.Logs.Difficulty vs SFS.WorldBase.Difficulty).
        # Resolve it properly: if any candidate full name still exists in 1.7,
        # that type kept its name and must map to ITSELF.
        cands = IDX16.get(real_p) or IDX23.get(real_p) or []
        survivors = [x for x in cands if x in T17]
        real_full = survivors[0] if survivors else real_full
        if not survivors:
            a = auth_full(obf_p, real_p)
            if a:
                real_full = a
                n_real = a[:-(len(real_p) + 1)] if a.endswith('.' + real_p) else ''
        top_real_full = (n_real + '.' + top_real) if n_real else top_real
        if not survivors and top_real_full in T17:
            # the PARENT kept its name, so the obf side lives in the SAME namespace;
            # only the leaf was renamed.  Without this the nested entry inherits the
            # ambiguous namespace and points at the wrong parent.
            n_obf = n_real
        obf_full_try = (n_obf + '.' + obf_p) if n_obf else obf_p
        if survivors and obf_full_try != real_full:
            identity_fixed += 1
            obf_full = real_full
            n_obf = n_real
        else:
            obf_full = obf_full_try
        if n_obf != n_real:
            moved += 1
        if real_full == obf_full:
            continue
        out.append((real_full, obf_full))

    path = os.path.join(HERE, 'bridge_map.tsv')
    with open(path, 'w', encoding='utf-8', newline='') as f:
        for a, b in sorted(out):
            f.write('%s\t%s\n' % (a, b))

    # ---- guard: a duplicate real key means two obf types were filed under one
    # name, so the loser is unreachable and any mod referencing it dies with
    # "TypeLoadException: Failure has occurred while loading a type."
    seen = collections.defaultdict(list)
    for a, b in out:
        seen[a].append(b)
    dups = {k: v for k, v in seen.items() if len(set(v)) > 1}
    if dups:
        print('\n!! DUPLICATE REAL KEYS (%d) - the map is ambiguous here:' % len(dups))
        for k in sorted(dups):
            print('   %-40s -> %s' % (k, ', '.join(sorted(set(dups[k])))))
    else:
        print('\nno duplicate real keys')

    # full pair list (identity included) for the member-level matcher.
    # IMPORTANT: this file is the keying source for member_map.tsv, so it needs
    # exactly the same namespace disambiguation as bridge_map.tsv above -
    # otherwise the member rows for the colliding types get read from the wrong
    # obfuscated type.
    pairs_path = os.path.join(HERE, 'type_pairs.tsv')
    npair = 0
    with open(pairs_path, 'w', encoding='utf-8', newline='') as f:
        f.write('real\tobf\n')
        for r in rows:
            obf, real = r['混淆名'], r['真名']
            if not real:
                continue
            obf_p = norm_plus(obf); real_p = norm_plus(real)
            n_obf = ns_obf.get(obf_p, ns_obf.get(obf_p.split('+')[0], ''))
            n_real = ns_real_of(real_p, real_p.split('+')[0])
            real_full = (n_real + '.' + real_p) if n_real else real_p
            a = auth_full(obf_p, real_p)
            if a:
                real_full = a
            f.write('%s\t%s\n' % (real_full,
                                  (n_obf + '.' + obf_p) if n_obf else obf_p))
            npair += 1
    print('type_pairs.tsv: %d 对（含名字未变的）' % npair)

    print('映射条数: %d  (未解析 %d ; 命名空间搬动 %d ; 同名强制恒等 %d)' % (len(out), nores, moved, identity_fixed))
    print('样例:')
    for a, b in sorted(out)[:4]:
        print('   %-46s -> %s' % (a, b))
    print('   ...')
    for want in ('ModLoader.Mod', 'ModLoader.IMod', 'ModLoader.IUnloadableMod',
                 'ModLoader.ModKeybindings', 'ModLoader.Loader',
                 'SFS.UI.ModGUI.Window', 'SFS.UI.ModGUI.GUIElement',
                 'SFS.UI.ModGUI.Builder', 'SFS.ConvexPolygon', 'SFS.UI.BasicMenu'):
        hit = [b for a, b in out if a == want]
        print('   %-30s -> %s' % (want, hit[0] if hit else '** 不在映射里 **'))
    print('\nwrote %s' % path)

if __name__ == '__main__':
    main()
