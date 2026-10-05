// fixsig.cs — repair signature-blind member pairings.
//
// usage: fixsig.exe <cleanAsm> <obfAsm> <bridge_map.tsv> <member_map.tsv> <out.tsv>
//
// MemberMap pairs methods by IL-body hash, which ignores the signature.  Two
// methods with identical bodies but different return types (Builder.CreateSeparator
// -> SFS.UI.ModGUI.Separator vs CreateSpace -> Space) are therefore swapped, and the
// mod then calls the wrong one: "Method not found: BOJNCONNAOM ...AEJOODCDNBB(Transform,int,int,int)".
//
// Here every row's SHAPE (parameter types + return type, both normalised through
// the type map) is checked.  Inside one declaring-type pair the mismatched rows are
// re-paired by shape; a row whose shape matches is left alone.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

static class FixSig
{
    static Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.Ordinal);
    static AssemblyDefinition Clean, Obf;
    static int _checked, _ok, _fixed, _left;

    static string Norm(string cecilFull)
    {
        string plus = cecilFull.Replace('/', '+');
        string o;
        if (T.TryGetValue(plus, out o)) return o;      // real name -> obf name
        return plus;
    }

    static string Shape(TypeReference t)
    {
        if (t == null) return "void";
        if (t is ArrayType a) return Shape(a.ElementType) + "[]";
        if (t is ByReferenceType b) return Shape(b.ElementType) + "&";
        if (t is PointerType p) return Shape(p.ElementType) + "*";
        if (t is GenericInstanceType g)
        {
            var sb = new StringBuilder(Norm(g.ElementType.FullName)); sb.Append('<');
            for (int i = 0; i < g.GenericArguments.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Shape(g.GenericArguments[i])); }
            sb.Append('>'); return sb.ToString();
        }
        if (t is GenericParameter gp) return "!" + gp.Position;
        return Norm(t.FullName);
    }

    static string MethodShape(MethodDefinition m)
    {
        var sb = new StringBuilder();
        foreach (var p in m.Parameters) { sb.Append(Shape(p.ParameterType)); sb.Append(','); }
        sb.Append("->"); sb.Append(Shape(m.ReturnType));
        return sb.ToString();
    }

    static MethodDefinition FindMethod(AssemblyDefinition asm, string typeFull, string name)
    {
        var t = asm.MainModule.GetType(typeFull.Replace('+', '/'));
        if (t == null) return null;
        var ms = t.Methods.Where(x => x.Name == name).ToList();
        return ms.Count == 1 ? ms[0] : (ms.Count == 0 ? null : ms[0]);
    }

    static int Main(string[] a)
    {
        try
        {
            foreach (var line in File.ReadAllLines(a[2], Encoding.UTF8))
            { var c = line.Split('\t'); if (c.Length == 2 && c[0].Length > 0) T[c[0]] = c[1]; }
            Clean = AssemblyDefinition.ReadAssembly(a[0]);
            Obf = AssemblyDefinition.ReadAssembly(a[1]);

            var lines = File.ReadAllLines(a[3], Encoding.UTF8);
            var outRows = new List<string[]>();
            var header = lines[0].Split('\t');
            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Trim().Length == 0) continue;
                var c = lines[i].Split('\t');
                if (c.Length < 6) continue;
                outRows.Add(c);
            }

            // group by (realType, obfType) so re-pairing stays inside one type
            var groups = outRows.GroupBy(r => r[3] + "\u0001" + r[1]);
            foreach (var grp in groups)
            {
                var bad = new List<string[]>();     // rows whose shape does not match
                foreach (var r in grp)
                {
                    _checked++;
                    string shapeReal = null, shapeObf = null;
                    if (r[0] == "method")
                    {
                        var rm = FindMethod(Clean, r[3], r[4]);
                        var om = FindMethod(Obf, r[1], r[2]);
                        if (rm != null) shapeReal = MethodShape(rm);
                        if (om != null) shapeObf = MethodShape(om);
                    }
                    else if (r[0] == "field")
                    {
                        var rt = Clean.MainModule.GetType(r[3].Replace('+', '/'));
                        var ot = Obf.MainModule.GetType(r[1].Replace('+', '/'));
                        var rf = rt != null ? rt.Fields.FirstOrDefault(x => x.Name == r[4]) : null;
                        var of = ot != null ? ot.Fields.FirstOrDefault(x => x.Name == r[2]) : null;
                        if (rf != null) shapeReal = Shape(rf.FieldType);
                        if (of != null) shapeObf = Shape(of.FieldType);
                    }
                    else continue;                  // properties: accessors carry the shape

                    if (shapeReal == null || shapeObf == null) continue;
                    if (shapeReal == shapeObf) { _ok++; continue; }
                    bad.Add(r);
                }
                if (bad.Count == 0) continue;

                // collect the obf members NOT already correctly claimed inside this group
                var usedObf = new HashSet<string>(StringComparer.Ordinal);
                foreach (var r in grp) if (!bad.Contains(r)) usedObf.Add(r[0] + "|" + r[2]);
                var candidates = new List<KeyValuePair<string, string[]>>();   // shape -> row
                foreach (var r in bad)
                {
                    var om = FindMethod(Obf, r[1], r[2]);
                    string sh = om != null ? MethodShape(om) : null;
                    if (sh != null) candidates.Add(new KeyValuePair<string, string[]>(sh, r));
                }
                // what shape does each bad real member actually need?
                int fixedHere = 0;
                foreach (var r in bad)
                {
                    string need = null;
                    if (r[0] == "method")
                    {
                        var rm = FindMethod(Clean, r[3], r[4]);
                        if (rm != null) need = MethodShape(rm);
                    }
                    else if (r[0] == "field")
                    {
                        var rt = Clean.MainModule.GetType(r[3].Replace('+', '/'));
                        var rf = rt != null ? rt.Fields.FirstOrDefault(x => x.Name == r[4]) : null;
                        if (rf != null) need = Shape(rf.FieldType);
                    }
                    if (need == null) continue;

                    // find an obf method of this type with exactly that shape and not yet used
                    var t = Obf.MainModule.GetType(r[1].Replace('+', '/'));
                    if (t == null) continue;
                    var hits = r[0] == "method"
                        ? t.Methods.Where(x => MethodShape(x) == need && !usedObf.Contains("method|" + x.Name)).ToList()
                        : new List<MethodDefinition>();
                    if (r[0] == "field")
                    {
                        var f = t.Fields.FirstOrDefault(x => Shape(x.FieldType) == need && !usedObf.Contains("field|" + x.Name));
                        if (f != null)
                        {
                            Console.WriteLine("  FIX field " + r[3] + "::" + r[4] + " : " + r[2] + " -> " + f.Name);
                            r[2] = f.Name; usedObf.Add("field|" + f.Name); _fixed++; fixedHere++;
                        }
                        continue;
                    }
                    if (hits.Count == 1)
                    {
                        Console.WriteLine("  FIX method " + r[3] + "::" + r[4] + " : " + r[2] + " -> " + hits[0].Name
                                          + "   (shape " + need + ")");
                        usedObf.Remove("method|" + r[2]);
                        r[2] = hits[0].Name; usedObf.Add("method|" + hits[0].Name); _fixed++; fixedHere++;
                    }
                    else _left++;
                }
            }

            using (var w = new StreamWriter(a[4], false, new UTF8Encoding(false)))
            {
                w.WriteLine(string.Join("\t", header));
                foreach (var r in outRows) w.WriteLine(string.Join("\t", r));
            }
            Console.WriteLine("checked " + _checked + ", shape-ok " + _ok + ", repaired " + _fixed + ", unresolved " + _left);
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e); return 2; }
        return 0;
    }
}
