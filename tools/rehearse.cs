// rehearse.cs — run the TypeBridge rewrite pipeline offline (no game process).
//
// usage: rehearse.exe [gameRoot] [workDirWithTypeBridge.dll]
using System;
using System.IO;
using System.Reflection;
using System.Text;

static class Rehearse
{
    static Assembly Resolve(object s, ResolveEventArgs e)
    {
        try
        {
            string name = new AssemblyName(e.Name).Name;
            string gameRoot = Environment.GetEnvironmentVariable("SFS17_ROOT") ??
                              @"D:\2SFS Project\Spaceflight Simulator1.7";
            string managed = Path.Combine(gameRoot, "Spaceflight Simulator_Data", "Managed");
            foreach (var dir in new[] { managed, AppDomain.CurrentDomain.BaseDirectory })
            {
                string cand = Path.Combine(dir, name + ".dll");
                if (File.Exists(cand)) return Assembly.LoadFrom(cand);
            }
        }
        catch { }
        return null;
    }

    static int Main(string[] args)
    {
        string gameRoot = args.Length > 0 ? args[0] : @"D:\2SFS Project\Spaceflight Simulator1.7";
        Environment.SetEnvironmentVariable("SFS17_ROOT", gameRoot);
        AppDomain.CurrentDomain.AssemblyResolve += Resolve;
        try
        {
            var t = Type.GetType("TypeBridge, TypeBridge", true);
            var m = t.GetMethod("Rehearse", BindingFlags.Public | BindingFlags.Static);
            m.Invoke(null, new object[] { gameRoot });
            Console.WriteLine("rehearse ok");
            return 0;
        }
        catch (Exception e)
        {
            Console.WriteLine("rehearse FAILED: " + e);
            return 1;
        }
    }
}
