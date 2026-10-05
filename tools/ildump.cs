// ildump.cs — find the methods that use a string literal and dump their IL.
// usage: ildump.exe <assembly.dll> <literal-substring> [methodNameFilter]
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class IlDump
{
    static int Main(string[] a)
    {
        try { return Run(a); } catch (Exception e) { Console.WriteLine("FATAL " + e.Message); return 2; }
    }

    static int Run(string[] a)
    {
        var asm = AssemblyDefinition.ReadAssembly(a[0]);
        string needle = a[1];
        string filt = a.Length > 2 ? a[2] : null;
        foreach (var t in asm.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                if (filt != null && m.Name.IndexOf(filt, StringComparison.OrdinalIgnoreCase) < 0) continue;
                bool hit = m.Body.Instructions.Any(i => i.OpCode.Code == Code.Ldstr &&
                                                        i.Operand is string s && s.Contains(needle));
                if (!hit) continue;
                Console.WriteLine("################ " + t.FullName.Replace('/', '+') + "." + m.Name
                                  + "   (" + m.Body.Instructions.Count + " insns)");
                foreach (var ins in m.Body.Instructions)
                {
                    string op = "";
                    if (ins.Operand is string s) op = "\"" + s + "\"";
                    else if (ins.Operand is MethodReference mr) op = mr.DeclaringType.Name + "::" + mr.Name;
                    else if (ins.Operand is FieldReference fr) op = fr.DeclaringType.Name + "::" + fr.Name;
                    else if (ins.Operand is TypeReference tr) op = tr.Name;
                    else if (ins.Operand != null) op = ins.Operand.ToString();
                    Console.WriteLine(string.Format("  IL_{0:X4}  {1,-12} {2}", ins.Offset, ins.OpCode.Name, op));
                }
                Console.WriteLine();
            }
        return 0;
    }
}
