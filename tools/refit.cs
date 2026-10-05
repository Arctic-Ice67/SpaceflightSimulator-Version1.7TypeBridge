// refit.cs — re-pair types the structural matcher got wrong (evidence ~0).
//
// usage: refit.exe <cleanAsm> <obfAsm> <bridge_map.tsv> <realTypeName> [realTypeName...]
//
// Builds a SIGNATURE MULTISET for the real type and for every 1.7 type of the same
// kind, with every type name normalised to its 1.7 name through bridge_map.  A real
// type can only be its counterpart if the multisets are equal - which pins down
// interfaces far better than the "score 0.6, all signals 0.0" the old matcher used.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

static class Refit
{
    static Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.Ordinal);
    static AssemblyDefinition Clean, Obf;

    static string Norm(string cecilFull)
    {
        string plus = cecilFull.Replace('/', '+');
        string o; return T.TryGetValue(plus, out o) ? o : plus;
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

    static List<string> SigSet(TypeDefinition t)
    {
        var l = new List<string>();
        foreach (var m in t.Methods)
        {
            if (m.IsGetter || m.IsSetter) { l.Add("prop:" + Shape(m.ReturnType) + (m.IsSetter ? "<-" + Shape(m.Parameters[0].ParameterType) : "")); continue; }
            var sb = new StringBuilder();
            foreach (var p in m.Parameters) { sb.Append(Shape(p.ParameterType)); sb.Append(','); }
            sb.Append("->").Append(Shape(m.ReturnType));
            l.Add(sb.ToString());
        }
        foreach (var f in t.Fields) l.Add("f:" + Shape(f.FieldType));
        l.Sort(StringComparer.Ordinal);
        return l;
    }

    static int Main(string[] a)
    {
        try
        {
            foreach (var line in File.ReadAllLines(a[2], Encoding.UTF8))
            { var c = line.Split('\t'); if (c.Length == 2 && c[0].Length > 0) T[c[0]] = c[1]; }
            Clean = AssemblyDefinition.ReadAssembly(a[0]);
            Obf = AssemblyDefinition.ReadAssembly(a[1]);

            var obfTypes = Obf.MainModule.GetTypes().ToList();
            for (int i = 3; i < a.Length; i++)
            {
                string want = a[i];
                var rt = Clean.MainModule.GetType(want.Replace('+', '/'));
                Console.WriteLine("================ " + want + (rt == null ? "   ** not found in clean **" : "   kind=" + (rt.IsInterface ? "interface" : rt.IsEnum ? "enum" : "class")) + " ================");
                if (rt == null) continue;
                var rs = SigSet(rt);
                Console.WriteLine("  real members: " + rs.Count);
                var hits = new List<KeyValuePair<int, TypeDefinition>>();
                foreach (var ot in obfTypes)
                {
                    if (ot.IsInterface != rt.IsInterface) continue;
                    if (ot.IsEnum != rt.IsEnum) continue;
                    if (ot.Methods.Count + ot.Fields.Count != rs.Count) continue;
                    var os = SigSet(ot);
                    if (os.Count != rs.Count) continue;
                    int same = 0;
                    for (int k = 0; k < rs.Count; k++) if (rs[k] == os[k]) same++;
                    if (same == rs.Count) hits.Add(new KeyValuePair<int, TypeDefinition>(same, ot));
                }
                Console.WriteLine("  EXACT signature-set matches: " + hits.Count);
                foreach (var h in hits.Take(8))
                    Console.WriteLine("      " + h.Value.FullName + "   (current mapping: " + (T.ContainsKey(want) ? T[want] : want) + ")");
            }
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e); return 2; }
        return 0;
    }
}
