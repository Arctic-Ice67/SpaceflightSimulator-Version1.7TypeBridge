// proporder.cs — list a type's properties in METADATA order (Beebyte preserves rows).
// usage: proporder.exe <assembly.dll> <TypeFullName>
using System;
using System.IO;
using System.Linq;
using Mono.Cecil;

static class PropOrder
{
    static int Main(string[] a)
    {
        try
        {
            var asm = AssemblyDefinition.ReadAssembly(a[0]);
            string want = a[1].Replace('/', '+');
            foreach (var t in asm.MainModule.GetTypes().Where(x => x.FullName.Replace('/', '+') == want))
            {
                Console.WriteLine("=== " + Path.GetFileName(a[0]) + " :: " + want + " (" + t.Properties.Count + " props) ===");
                int i = 0;
                foreach (var p in t.Properties)
                {
                    string g = p.GetMethod != null ? p.GetMethod.Name : "-";
                    Console.WriteLine(string.Format("  {0,2}. prop {1,-22} getter {2}", ++i, p.Name, g));
                }
                Console.WriteLine("  -- methods in metadata order --");
                i = 0;
                foreach (var m in t.Methods)
                    Console.WriteLine(string.Format("  {0,2}. {1}  abs={2}", ++i, m.Name, m.IsAbstract ? 1 : 0));
            }
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e.Message); return 2; }
        return 0;
    }
}
