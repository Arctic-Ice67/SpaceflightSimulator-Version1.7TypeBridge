// genparammap.cs — parameter-name table for Harmony patch rewriting.
//
// usage: genparammap.exe <cleanAssembly-CSharp.dll> <bridge_map.tsv> <out.tsv>
//
// Harmony matches a [HarmonyPatch] prefix/postfix parameter to the ORIGINAL
// method's parameter BY NAME.  Beebyte renames parameter names too
// (DrawRegionalOutline's `color` became `ANPAOOACJBJ`), so every such patch
// dies with 'Parameter "color" not found in method ...'.  This table lets the
// bridge translate the patch parameter to Harmony's positional form `__N`,
// which needs nothing but the original's parameter ORDER.
//
//   realType|realMethod|paramCount <TAB> name0,name1,...
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

static class GenParamMap
{
    static string RealFull(TypeReference t)
    {
        var chain = new List<string>();
        var cur = t;
        while (cur != null) { chain.Insert(0, cur.Name); cur = cur.DeclaringType; }
        string ns = t.Namespace;
        if (string.IsNullOrEmpty(ns))
        {
            var root = t; int g = 0;
            while (root.DeclaringType != null && g++ < 40) root = root.DeclaringType;
            ns = root.Namespace;
        }
        string rel = string.Join("+", chain.ToArray());
        return string.IsNullOrEmpty(ns) ? rel : ns + "." + rel;
    }

    static int Main(string[] a)
    {
        try
        {
            var asm = AssemblyDefinition.ReadAssembly(a[0]);
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var line in File.ReadAllLines(a[1], Encoding.UTF8))
            {
                var c = line.Split('\t');
                if (c.Length == 2) keep.Add(c[0]);
            }
            Console.WriteLine("bridge_map keys: " + keep.Count);

            int n = 0;
            using (var w = new StreamWriter(a[2], false, new UTF8Encoding(false)))
            {
                foreach (var t in asm.MainModule.GetTypes())
                {
                    string full = RealFull(t);
                    if (!keep.Contains(full)) continue;
                    foreach (var m in t.Methods)
                    {
                        if (m.Parameters.Count == 0 || m.Parameters.Count > 16) continue;
                        bool any = false;
                        foreach (var p in m.Parameters) if (!string.IsNullOrEmpty(p.Name)) { any = true; break; }
                        if (!any) continue;
                        var names = m.Parameters.Select(p => string.IsNullOrEmpty(p.Name) ? "arg" + p.Index : p.Name);
                        w.Write(full); w.Write('|'); w.Write(m.Name); w.Write('|');
                        w.Write(m.Parameters.Count); w.Write('\t');
                        w.Write(string.Join(",", names.ToArray()));
                        w.Write('\n');
                        n++;
                    }
                }
            }
            Console.WriteLine("param rows: " + n + "  (" + new FileInfo(a[2]).Length + " bytes)");
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e); return 2; }
        return 0;
    }
}
