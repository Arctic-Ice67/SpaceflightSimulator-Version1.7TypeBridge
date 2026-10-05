// verify_rewritten.cs — audit the EXACT rewritten mod images the game loaded.
//
// usage: verify_rewritten.exe [gameRoot] [tempDir] [outTxt]
//
// Reads %TEMP%\sfs17rtbridge\<Mod>\<Mod>.dll (the images TypeBridge handed to
// Assembly.LoadFrom) and reports everything that would make the CLR throw
// TypeLoadException / silently skip a mod:
//
//   1 [NORESOLVE]   a TypeRef scoped to Assembly-CSharp that does not exist in the
//                   obfuscated assembly  -> TypeLoadException when JITted
//   2 [OVERRIDE]    an `override` method whose obfuscated name matches NO virtual
//                   slot on the game base chain -> the mod's Load()/Early_Load()
//                   is never called, silently (loads, does nothing)
//   3 [ABSTRACT]    an abstract slot of the game base that the mod no longer
//                   implements after renaming -> TypeLoadException at vtable setup
//   4 [ASSEMBLY]    unresolved assembly references
//
// Signature comparison substitutes the generic arguments of the base chain, so
// `override bool IsEqual(GameObject,GameObject)` on `Obs<GameObject>` correctly
// matches the open slot `IsEqual(!0,!0)`.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

static class VerifyRewritten
{
    static AssemblyDefinition Obf;
    static readonly HashSet<string> ObfTypes = new HashSet<string>(StringComparer.Ordinal);
    static readonly Dictionary<string, string> T = new Dictionary<string, string>(StringComparer.Ordinal);
    static readonly StringBuilder R = new StringBuilder();
    static int _bad, _overrideMiss, _abstractMiss, _noResolve;

    static void W(string s) { Console.WriteLine(s); R.AppendLine(s); }

    class Res : DefaultAssemblyResolver
    {
        public AssemblyDefinition Cur;
        public override AssemblyDefinition Resolve(AssemblyNameReference n)
        {
            if (n.Name == "Assembly-CSharp" && Cur != null) return Cur;
            try { return base.Resolve(n); } catch { return null; }
        }
    }
    static Res _res;

    static TypeReference Strip(TypeReference t)
    {
        while (t is TypeSpecification) t = ((TypeSpecification)t).ElementType;
        return t;
    }

    static bool FromGame(TypeReference t)
    {
        if (t == null) return false;
        if (t is GenericParameter) return false;
        t = Strip(t);
        var s = t.Scope;
        if (s is AssemblyNameReference a) return a.Name == "Assembly-CSharp";
        if (s is ModuleDefinition m) return m.Assembly.Name.Name == "Assembly-CSharp";
        return false;
    }

    static TypeDefinition ObfType(string full)
    {
        if (string.IsNullOrEmpty(full)) return null;
        try { return Obf.MainModule.GetType(full.Replace('+', '/')); } catch { return null; }
    }

    // ------------------------------------------------------- generic substitution
    static TypeReference Sub(TypeReference t, Dictionary<int, TypeReference> sub)
    {
        if (t == null) return null;
        if (t is GenericParameter gp)
        {
            TypeReference r;
            if (gp.Type == GenericParameterType.Type && sub != null && sub.TryGetValue(gp.Position, out r)) return r;
            return gp;
        }
        if (t is ArrayType a) return new ArrayType(Sub(a.ElementType, sub));
        if (t is ByReferenceType b) return new ByReferenceType(Sub(b.ElementType, sub));
        if (t is PointerType p) return new PointerType(Sub(p.ElementType, sub));
        if (t is GenericInstanceType g)
        {
            var ng = new GenericInstanceType(g.ElementType);
            foreach (var ga in g.GenericArguments) ng.GenericArguments.Add(Sub(ga, sub));
            return ng;
        }
        return t;
    }

    static string Key(TypeReference t, Dictionary<int, TypeReference> sub)
    {
        if (t == null) return "void";
        t = Sub(t, sub);
        if (t is ArrayType a) return Key(a.ElementType, null) + "[]";
        if (t is ByReferenceType b) return Key(b.ElementType, null) + "&";
        if (t is PointerType p) return Key(p.ElementType, null) + "*";
        if (t is GenericInstanceType g)
        {
            var sb = new StringBuilder(Strip(g).FullName); sb.Append('<');
            for (int i = 0; i < g.GenericArguments.Count; i++) { if (i > 0) sb.Append(','); sb.Append(Key(g.GenericArguments[i], null)); }
            sb.Append('>'); return sb.ToString();
        }
        if (t is GenericParameter gp) return "!" + gp.Position + (gp.Type == GenericParameterType.Method ? "M" : "T");
        var el = Strip(t);
        if (FromGame(el)) return "G:" + el.FullName;      // obf name on both sides after the rewrite
        return "X:" + el.FullName;
    }

    static string Sig(MethodReference m, Dictionary<int, TypeReference> sub)
    {
        var sb = new StringBuilder(m.Name); sb.Append('(');
        for (int i = 0; i < m.Parameters.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append(Key(m.Parameters[i].ParameterType, sub));
        }
        sb.Append(')');
        return sb.ToString();
    }

    static string Index(MethodReference m, Dictionary<int, TypeReference> sub)
    {
        var sb = new StringBuilder(m.Name); sb.Append('|').Append(m.GenericParameters.Count).Append('|');
        for (int i = 0; i < m.Parameters.Count; i++) { sb.Append(Key(m.Parameters[i].ParameterType, sub)); sb.Append(','); }
        return sb.ToString();
    }

    // ---------------------------------------------------------------- base chains
    class Link { public TypeDefinition Def; public Dictionary<int, TypeReference> Sub; }

    static List<Link> ChainOf(TypeReference startRef)
    {
        var list = new List<Link>();
        if (startRef == null) return list;
        TypeDefinition def = null;
        try { def = startRef.Resolve(); } catch { }
        if (def == null) return list;
        // the starting reference may itself be generic (Obs<GameObject>)
        var sub = new Dictionary<int, TypeReference>();
        var git0 = startRef as GenericInstanceType;
        if (git0 != null)
            for (int i = 0; i < def.GenericParameters.Count && i < git0.GenericArguments.Count; i++)
                sub[i] = git0.GenericArguments[i];
        int g = 0;
        while (def != null && g++ < 24)
        {
            list.Add(new Link { Def = def, Sub = sub });
            var bt = def.BaseType;
            if (bt == null) break;
            var bts = Sub(bt, sub);
            TypeDefinition btd = null;
            try { btd = bts.Resolve(); } catch { }
            if (btd == null) break;
            var nsub = new Dictionary<int, TypeReference>();
            var git = bts as GenericInstanceType;
            if (git != null)
                for (int i = 0; i < btd.GenericParameters.Count && i < git.GenericArguments.Count; i++)
                    nsub[i] = git.GenericArguments[i];
            def = btd; sub = nsub;
        }
        return list;
    }

    // ---------------------------------------------------------------- surface dump
    static void DumpSurface(string label, string obfFull)
    {
        var t = ObfType(obfFull);
        W("");
        W("---- " + label + "  (" + obfFull + ") ----");
        if (t == null) { W("   !! type not found"); _bad++; return; }
        foreach (var lnk in ChainOf(t))
        {
            W("   [" + lnk.Def.FullName + "]  abstract=" + lnk.Def.IsAbstract);
            foreach (var m in lnk.Def.Methods.Where(x => x.IsVirtual || x.IsAbstract).OrderBy(x => x.Name))
                W(string.Format("      {0,-24} {1,-56} abs={2}", m.Name, Sig(m, lnk.Sub), m.IsAbstract ? 1 : 0));
        }
    }

    // ---------------------------------------------------------------- ref checks
    static void Refs(IEnumerable<TypeReference> src, string where, HashSet<string> reported)
    {
        foreach (var raw in src)
        {
            if (raw is GenericParameter) continue;
            var el = Strip(raw);
            if (el == null || el is GenericParameter || !FromGame(el)) continue;
            string fn = el.FullName;
            if (ObfTypes.Contains(fn)) continue;
            if (!reported.Add(fn)) continue;
            _bad++; _noResolve++;
            W("   [NORESOLVE] " + where + "\n                 -> " + fn + "   (no such type in obf Assembly-CSharp)");
        }
    }

    static IEnumerable<TypeReference> TypeRefsOfMember(MethodDefinition m)
    {
        var list = new List<TypeReference>();
        if (m.ReturnType != null) list.Add(m.ReturnType);
        foreach (var p in m.Parameters) list.Add(p.ParameterType);
        foreach (var gp in m.GenericParameters) foreach (var c in gp.Constraints) list.Add(c.ConstraintType);
        if (m.HasBody)
        {
            foreach (var v in m.Body.Variables) list.Add(v.VariableType);
            foreach (var ins in m.Body.Instructions)
            {
                var o = ins.Operand;
                if (o is TypeReference tr) list.Add(tr);
                else if (o is FieldReference fr) { list.Add(fr.DeclaringType); list.Add(fr.FieldType); }
                else if (o is MethodReference mr)
                {
                    list.Add(mr.DeclaringType);
                    list.Add(mr.ReturnType);
                    foreach (var p in mr.Parameters) list.Add(p.ParameterType);
                }
            }
        }
        return list;
    }

    // ---------------------------------------------------------------- main
    static int Main(string[] argv)
    {
        try { return Run(argv); }
        catch (Exception e)
        {
            string f = @"D:\2SFS Project\work\sfs17_compare\verify_rewritten.txt";
            try { File.AppendAllText(f, "\r\nFATAL: " + e.ToString() + "\r\n", new UTF8Encoding(false)); } catch { }
            try { Console.Out.Write("FATAL: " + e.GetType().FullName + ": " + e.Message + "\r\n" + e.StackTrace + "\r\n"); } catch { }
            return 2;
        }
    }

    static int Run(string[] argv)
    {
        string gameRoot = argv.Length > 0 ? argv[0] : @"D:\2SFS Project\Spaceflight Simulator1.7";
        string tempDir = argv.Length > 1 ? argv[1] : Path.Combine(Path.GetTempPath(), "sfs17rtbridge");
        string outFile = argv.Length > 2 ? argv[2] : @"D:\2SFS Project\work\sfs17_compare\verify_rewritten.txt";
        string work = Path.GetDirectoryName(Path.GetFullPath(outFile));

        foreach (var line in File.ReadAllLines(Path.Combine(work, "bridge_map.tsv"), Encoding.UTF8))
        {
            var c = line.Split('\t');
            if (c.Length == 2 && c[0].Length > 0) T[c[0]] = c[1];
        }

        string managed = Path.Combine(gameRoot, "Spaceflight Simulator_Data", "Managed");
        _res = new Res();
        _res.AddSearchDirectory(managed);
        _res.AddSearchDirectory(tempDir);
        Obf = AssemblyDefinition.ReadAssembly(Path.Combine(managed, "Assembly-CSharp.dll"),
                                              new ReaderParameters { AssemblyResolver = _res });
        _res.Cur = Obf;
        foreach (var t in Obf.MainModule.GetTypes()) ObfTypes.Add(t.FullName);
        W("obf Assembly-CSharp: " + ObfTypes.Count + " types");
        W("temp rewritten dir  : " + tempDir);

        foreach (var n in new[] { "ModLoader.Mod", "ModLoader.ModKeybindings" })
        {
            string o;
            if (T.TryGetValue(n, out o)) DumpSurface(n, o);
        }

        foreach (var modDir in Directory.GetDirectories(tempDir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            foreach (var dll in Directory.GetFiles(modDir, "*.dll").OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                W("");
                W("================ " + Path.GetFileName(modDir) + " / " + Path.GetFileName(dll) + " ================");
                AssemblyDefinition mod;
                try { mod = AssemblyDefinition.ReadAssembly(dll, new ReaderParameters { AssemblyResolver = _res }); }
                catch (Exception e) { W("   !! cannot read: " + e.Message); _bad++; continue; }

                var reported = new HashSet<string>(StringComparer.Ordinal);
                var modTypes = mod.MainModule.GetTypes().ToList();

                foreach (var t in modTypes)
                {
                    if (t.BaseType != null) Refs(new[] { t.BaseType }, t.FullName + " (base)", reported);
                    foreach (var i in t.Interfaces) Refs(new[] { i.InterfaceType }, t.FullName + " (iface)", reported);
                    foreach (var f in t.Fields) Refs(new[] { f.FieldType }, t.FullName + "::" + f.Name, reported);
                    foreach (var p in t.Properties) Refs(new[] { p.PropertyType }, t.FullName + "::" + p.Name, reported);
                    foreach (var m in t.Methods) Refs(TypeRefsOfMember(m), t.FullName + "::" + m.Name, reported);
                }

                // ---------- per type: override binding + abstract coverage
                foreach (var t in modTypes)
                {
                    var chain = ChainOf(t);
                    if (!chain.Any(l => FromGame(l.Def))) continue;      // no game base at all

                    // every virtual slot offered ANYWHERE along the chain.  Mod-side
                    // links count too: UITools' radio buttons override a MOD base class.
                    var slots = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
                    foreach (var lnk in chain)
                        foreach (var bm in lnk.Def.Methods)
                            if (bm.IsVirtual || bm.IsAbstract)
                            {
                                var k = Index(bm, lnk.Sub);
                                if (!slots.ContainsKey(k)) slots[k] = bm;
                            }

                    // 2 OVERRIDE
                    foreach (var m in t.Methods)
                    {
                        if (!m.IsVirtual) continue;
                        if (m.IsNewSlot && !m.HasOverrides) continue;
                        if (slots.ContainsKey(Index(m, null))) continue;
                        _bad++; _overrideMiss++;
                        W("   [OVERRIDE] " + t.FullName + "::" + Sig(m, null)
                          + "\n              no matching virtual slot anywhere in the base chain -> never called");
                    }

                    // 3 ABSTRACT.  Walk top-down: an implementation CLOSER to the mod
                    // type satisfies a slot that a further ancestor still declares
                    // abstract (SFS.UI.BasicMenu implements Screen_Base's members, so
                    // its subclasses need not).
                    if (!t.IsAbstract)
                    {
                        var provided = new HashSet<string>(StringComparer.Ordinal);
                        var absSeen = new HashSet<string>(StringComparer.Ordinal);
                        foreach (var lnk in chain)
                            foreach (var bm in lnk.Def.Methods)
                            {
                                if (!bm.IsVirtual && !bm.IsAbstract) continue;
                                var k = Index(bm, lnk.Sub);
                                if (bm.IsAbstract)
                                {
                                    if (!provided.Contains(k) && absSeen.Add(k))
                                    {
                                        _bad++; _abstractMiss++;
                                        W("   [ABSTRACT] " + t.FullName + " : missing "
                                          + lnk.Def.FullName + "::" + Sig(bm, lnk.Sub));
                                    }
                                }
                                else provided.Add(k);
                            }
                    }
                }

                // ---------- entry points
                foreach (var t in modTypes)
                {
                    var chain = ChainOf(t);
                    var gameBase = chain.Select(l => l.Def).FirstOrDefault(l => FromGame(l));
                    if (gameBase == null || gameBase.FullName == null || !gameBase.FullName.StartsWith("ModLoader")) continue;
                    W("   entry " + t.FullName + " : base " + gameBase.FullName);
                    var slots = new Dictionary<string, MethodDefinition>(StringComparer.Ordinal);
                    foreach (var lnk in chain)
                        foreach (var bm in lnk.Def.Methods)
                            if (bm.IsVirtual || bm.IsAbstract) { var k = Index(bm, lnk.Sub); if (!slots.ContainsKey(k)) slots[k] = bm; }
                    foreach (var m in t.Methods.Where(x => x.IsVirtual && (!x.IsNewSlot || x.HasOverrides)))
                    {
                        bool binds = slots.ContainsKey(Index(m, null));
                        W("        " + (binds ? "BINDS " : "MISS!! ") + Sig(m, null));
                        if (!binds) { _bad++; _overrideMiss++; }
                    }
                }
            }
        }

        W("");
        W("TOTAL PROBLEMS: " + _bad + "   (noresolve " + _noResolve + ", override " + _overrideMiss + ", abstract " + _abstractMiss + ")");
        File.WriteAllText(outFile, R.ToString(), new UTF8Encoding(false));
        Console.WriteLine();
        Console.WriteLine("written: " + outFile);
        return 0;
    }
}
