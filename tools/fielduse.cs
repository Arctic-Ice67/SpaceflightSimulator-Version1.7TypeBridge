// fielduse.cs — which methods read/write each field of a type?
// usage: fielduse.exe <assembly.dll> <DeclaringTypeFullName>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class FieldUse
{
    static int Main(string[] a)
    {
        try { return Run(a); } catch (Exception e) { Console.WriteLine("FATAL " + e.Message); return 2; }
    }

    static int Run(string[] a)
    {
        string dll = a[0], want = a[1].Replace('/', '+');
        var asm = AssemblyDefinition.ReadAssembly(dll);
        Console.WriteLine("=== " + Path.GetFileName(dll) + "  fields of " + want + " ===");
        foreach (var t in asm.MainModule.GetTypes().Where(x => x.FullName.Replace('/', '+') == want))
        {
            var names = new HashSet<string>(t.Fields.Select(f => f.Name), StringComparer.Ordinal);
            foreach (var f in t.Fields)
                Console.WriteLine("  FIELD " + f.Name + " : " + f.FieldType.FullName);

            var use = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (var n in names) use[n] = new List<string>();
            foreach (var tt in asm.MainModule.GetTypes())
                foreach (var m in tt.Methods)
                {
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        var fr = ins.Operand as FieldReference;
                        if (fr == null) continue;
                        if (fr.DeclaringType.FullName.Replace('/', '+') != want) continue;
                        if (!use.ContainsKey(fr.Name)) continue;
                        string who = tt.FullName.Replace('/', '+') + "." + m.Name;
                        if (!use[fr.Name].Contains(who)) use[fr.Name].Add(who);
                    }
                }
            Console.WriteLine("  ---- readers/writers ----");
            foreach (var kv in use.OrderBy(k => k.Key, StringComparer.Ordinal))
                Console.WriteLine(string.Format("  {0,-24} {1}", kv.Key,
                    kv.Value.Count == 0 ? "(never referenced)" : string.Join(", ", kv.Value.Take(4).ToArray())));

            // The static ctor initialises every field, and it does so in source
            // declaration order - which Beebyte preserves.  That sequence is a
            // 1:1 positional key across the obfuscation boundary.
            foreach (var m in t.Methods.Where(x => x.Name == ".cctor"))
            {
                if (!m.HasBody) continue;
                Console.WriteLine("  ---- .cctor store order ----");
                int i = 0;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Stsfld) continue;
                    var fr = ins.Operand as FieldReference;
                    if (fr == null) continue;
                    Console.WriteLine(string.Format("    {0,2}. {1}", ++i, fr.Name));
                }
            }
            // likewise the regular ctor, for instance fields
            foreach (var m in t.Methods.Where(x => x.Name == ".ctor"))
            {
                if (!m.HasBody) continue;
                Console.WriteLine("  ---- .ctor store order ----");
                int i = 0;
                foreach (var ins in m.Body.Instructions)
                {
                    if (ins.OpCode.Code != Code.Stfld) continue;
                    var fr = ins.Operand as FieldReference;
                    if (fr == null) continue;
                    Console.WriteLine(string.Format("    {0,2}. {1}", ++i, fr.Name));
                }
            }
        }
        return 0;
    }
}
