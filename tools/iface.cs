// iface.cs — how does a mod use a given game type?
// usage: iface.exe <modDll> <nameSubstring>
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Iface
{
    static int Main(string[] a)
    {
        try
        {
            var asm = AssemblyDefinition.ReadAssembly(a[0]);
            string want = a[1];
            Console.WriteLine("=== " + Path.GetFileName(a[0]) + "  references to *" + want + "* ===");

            foreach (var t in asm.MainModule.GetTypes())
            {
                // interfaces declared on this type
                foreach (var i in t.Interfaces)
                    if (i.InterfaceType.FullName.Contains(want) || (i.InterfaceType.Name != null && i.InterfaceType.Name.Contains(want)))
                        Console.WriteLine("  IMPLEMENTS  " + t.FullName.Replace('/', '+') + "  :  " + i.InterfaceType.FullName);

                // base type
                if (t.BaseType != null && (t.BaseType.FullName.Contains(want) || t.BaseType.Name.Contains(want)))
                    Console.WriteLine("  DERIVES     " + t.FullName.Replace('/', '+') + "  :  " + t.BaseType.FullName);

                foreach (var m in t.Methods)
                {
                    foreach (var ov in m.Overrides)
                        if (ov.DeclaringType.FullName.Contains(want))
                            Console.WriteLine("  METHODIMPL  " + t.FullName.Replace('/', '+') + "." + m.Name
                                              + "  ->  " + ov.DeclaringType.FullName + "::" + ov.Name);
                    if (!m.HasBody) continue;
                    foreach (var ins in m.Body.Instructions)
                    {
                        var mr = ins.Operand as MethodReference;
                        if (mr != null && mr.DeclaringType.FullName.Contains(want))
                            Console.WriteLine("  CALL        " + t.FullName.Replace('/', '+') + "." + m.Name
                                              + "  ->  " + mr.DeclaringType.FullName + "::" + mr.Name + "("
                                              + string.Join(",", mr.Parameters.Select(p => p.ParameterType.Name).ToArray()) + ")");
                        var fr = ins.Operand as FieldReference;
                        if (fr != null && fr.DeclaringType.FullName.Contains(want))
                            Console.WriteLine("  FIELD       " + t.FullName.Replace('/', '+') + "." + m.Name
                                              + "  ->  " + fr.DeclaringType.FullName + "::" + fr.Name);
                    }
                    // method signature mentions it
                    foreach (var p in m.Parameters)
                        if (p.ParameterType.Name != null && p.ParameterType.Name.Contains(want))
                            Console.WriteLine("  PARAMTYPE   " + t.FullName.Replace('/', '+') + "." + m.Name + " : " + p.ParameterType.FullName);
                }
                foreach (var f in t.Fields)
                    if (f.FieldType.Name != null && f.FieldType.Name.Contains(want))
                        Console.WriteLine("  FIELDTYPE   " + t.FullName.Replace('/', '+') + "." + f.Name + " : " + f.FieldType.FullName);
            }
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e.Message); return 2; }
        return 0;
    }
}
