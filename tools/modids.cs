// modids.cs — what does each mod report as ModNameID / Dependencies?
// usage: modids.exe <modDll> [modDll...]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class ModIds
{
    static int Main(string[] a)
    {
        try { return Run(a); } catch (Exception e) { Console.WriteLine("FATAL " + e.Message); return 2; }
    }

    static IEnumerable<string> Strs(MethodDefinition m, int depth)
    {
        var outp = new List<string>();
        if (m == null || !m.HasBody || depth > 3) return outp;
        foreach (var ins in m.Body.Instructions)
        {
            if (ins.OpCode.Code == Code.Ldstr) outp.Add((string)ins.Operand);
            else
            {
                var mr = ins.Operand as MethodReference;
                if (mr != null && (mr.Name == ".ctor" || mr.Name.StartsWith("get_") || mr.Name.StartsWith("Add")))
                    continue;
                if (mr != null)
                {
                    try
                    {
                        var d = mr.Resolve();
                        if (d != null && d.HasBody && d.Module == m.Module)
                            foreach (var s in Strs(d, depth + 1)) outp.Add(s + "  (via " + mr.Name + ")");
                    }
                    catch { }
                }
            }
        }
        return outp;
    }

    static int Run(string[] files)
    {
        foreach (var f in files)
        {
            AssemblyDefinition mod;
            try { mod = AssemblyDefinition.ReadAssembly(f); } catch { continue; }
            Console.WriteLine("================ " + Path.GetFileName(f) + " ================");
            foreach (var t in mod.MainModule.GetTypes())
            {
                var names = new HashSet<string>(t.Methods.Select(x => x.Name), StringComparer.Ordinal);
                // Mod subclass / anything that looks like an entry point
                bool entry = t.Name == "Main" || t.Name == "Entrypoint" || names.Contains("LMELKIJNFBH");
                if (!entry) continue;
                Console.WriteLine("  TYPE " + t.FullName + "   base=" + (t.BaseType == null ? "-" : t.BaseType.FullName));
                foreach (var m in t.Methods)
                {
                    if (!m.HasBody) continue;
                    var s = Strs(m, 0).ToList();
                    if (s.Count == 0) continue;
                    Console.WriteLine("     " + m.Name.PadRight(22) + " ->  " + string.Join(" | ", s.Take(4).ToArray()));
                }
            }
        }
        return 0;
    }
}
