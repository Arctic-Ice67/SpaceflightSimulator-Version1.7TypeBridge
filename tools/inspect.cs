// inspect.cs — dump one mod type's declaration against its 1.7 base chain.
// usage: inspect.exe <modDll> <TypeFullName> [gameRoot]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

static class Inspect
{
    static AssemblyDefinition Obf;
    static Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.Ordinal);
    static Dictionary<string, TypeDefinition> ByFull = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);

    class Res : DefaultAssemblyResolver
    {
        public AssemblyDefinition Cur;
        public override AssemblyDefinition Resolve(AssemblyNameReference n)
        {
            if (n.Name == "Assembly-CSharp" && Cur != null) return Cur;
            try { return base.Resolve(n); } catch { return null; }
        }
    }

    static TypeReference Strip(TypeReference t) { while (t is TypeSpecification) t = ((TypeSpecification)t).ElementType; return t; }
    static bool FromGame(TypeReference t)
    {
        if (t == null || t is GenericParameter) return false;
        t = Strip(t);
        var s = t.Scope;
        if (s is AssemblyNameReference a) return a.Name == "Assembly-CSharp";
        if (s is ModuleDefinition m) return m.Assembly.Name.Name == "Assembly-CSharp";
        return false;
    }
    static string RealFull(TypeReference t)
    {
        var chain = new List<string>(); var cur = t;
        while (cur != null) { chain.Insert(0, cur.Name); cur = cur.DeclaringType; }
        string ns = t.Namespace;
        if (string.IsNullOrEmpty(ns)) { var r = t; int g = 0; while (r.DeclaringType != null && g++ < 40) r = r.DeclaringType; ns = r.Namespace; }
        string rel = string.Join("+", chain.ToArray());
        return string.IsNullOrEmpty(ns) ? rel : ns + "." + rel;
    }
    static string Sig(MethodReference m)
    {
        var sb = new StringBuilder(m.Name); sb.Append('(');
        for (int i = 0; i < m.Parameters.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Strip(m.Parameters[i].ParameterType).FullName); }
        sb.Append(") : ").Append(Strip(m.ReturnType).FullName);
        return sb.ToString();
    }

    static int Main(string[] a)
    {
        try { return Run(a); } catch (Exception e) { Console.WriteLine("FATAL " + e); return 2; }
    }

    static int Run(string[] a)
    {
        string dll = a[0], want = a[1];
        string gameRoot = a.Length > 2 ? a[2] : @"D:\2SFS Project\Spaceflight Simulator1.7";
        string work = @"D:\2SFS Project\work\sfs17_compare";
        string managed = Path.Combine(gameRoot, "Spaceflight Simulator_Data", "Managed");
        foreach (var line in File.ReadAllLines(Path.Combine(work, "bridge_map.tsv"), Encoding.UTF8))
        { var c = line.Split('\t'); if (c.Length == 2 && c[0].Length > 0) T[c[0]] = c[1]; }

        var res = new Res(); res.AddSearchDirectory(managed); res.AddSearchDirectory(Path.GetDirectoryName(dll));
        Obf = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"), new ReaderParameters { AssemblyResolver = res });
        res.Cur = Obf;
        foreach (var td in Obf.MainModule.GetTypes()) { string k = td.FullName.Replace('/', '+'); if (!ByFull.ContainsKey(k)) ByFull[k] = td; }

        var mod = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = res });
        Console.WriteLine("=== " + Path.GetFileName(dll) + "  type " + want + " ===");

        foreach (var t in mod.MainModule.GetTypes().Where(x => x.FullName.Replace('/', '+') == want))
        {
            Console.WriteLine("BASE REF   : " + (t.BaseType == null ? "(none)" : t.BaseType.FullName + "   [game=" + FromGame(t.BaseType) + "]"));
            Console.WriteLine("DECLARED METHODS:");
            foreach (var m in t.Methods)
                Console.WriteLine(string.Format("   {0,-70} virt={1} newslot={2} abs={3}",
                    Sig(m), m.IsVirtual ? 1 : 0, m.IsNewSlot ? 1 : 0, m.IsAbstract ? 1 : 0));
        }

        // walk to the game base
        foreach (var t in mod.MainModule.GetTypes().Where(x => x.FullName.Replace('/', '+') == want))
        {
            var cur = t.BaseType; int g = 0; TypeReference gameRef = null;
            while (cur != null && g++ < 40)
            {
                if (FromGame(cur)) { gameRef = cur; break; }
                TypeDefinition rd = null; try { rd = cur.Resolve(); } catch { }
                if (rd == null) break;
                cur = rd.BaseType;
            }
            if (gameRef == null) { Console.WriteLine("no game base"); continue; }
            string real = RealFull(Strip(gameRef));
            string obf; if (!T.TryGetValue(real, out obf)) obf = real;
            Console.WriteLine("\nGAME BASE  : real=" + real + "  obf=" + obf + "   ref=" + gameRef.FullName);
            TypeDefinition def;
            if (!ByFull.TryGetValue(obf, out def)) { Console.WriteLine("   !! not found in obf"); continue; }

            // substitutions from the (possibly generic) base reference
            var sub = new Dictionary<int, TypeReference>();
            var git = gameRef as GenericInstanceType;
            if (git != null)
                for (int i = 0; i < def.GenericParameters.Count && i < git.GenericArguments.Count; i++) sub[i] = git.GenericArguments[i];

            var d = def; var ds = sub; int g2 = 0;
            while (d != null && g2++ < 20)
            {
                Console.WriteLine("   [" + d.FullName + "]  abstract=" + d.IsAbstract);
                foreach (var bm in d.Methods.Where(x => x.IsVirtual || x.IsAbstract).OrderBy(x => x.Name))
                {
                    var sb = new StringBuilder(bm.Name); sb.Append('(');
                    for (int i = 0; i < bm.Parameters.Count; i++)
                    {
                        if (i > 0) sb.Append(',');
                        var pt = bm.Parameters[i].ParameterType;
                        TypeReference r;
                        if (pt is GenericParameter gp && gp.Type == GenericParameterType.Type && ds.TryGetValue(gp.Position, out r)) sb.Append(Strip(r).FullName);
                        else sb.Append(Strip(pt).FullName);
                    }
                    sb.Append(')');
                    Console.WriteLine(string.Format("      {0,-62} abs={1}", sb, bm.IsAbstract ? 1 : 0));
                }
                var bt = d.BaseType; if (bt == null) break;
                TypeDefinition btd = null; try { btd = bt.Resolve(); } catch { }
                if (btd == null) break;
                d = btd; ds = new Dictionary<int, TypeReference>();
            }
        }
        return 0;
    }
}
