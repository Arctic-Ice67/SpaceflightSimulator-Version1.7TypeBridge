//SFS1.7运行时mod改写桥
//让未修改的1.6编译DLLmod跑在Beebyte混淆过的1.7Assembly-CSharp上

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;
using MonoMod.RuntimeDetour;

public static class TypeBridge
{
    //映射表
    static Dictionary<string, string> _type;          //混淆类型
    static Dictionary<string, string> _member;        //混淆成员
    static Dictionary<string, string> _memberLow;     //低置信度混淆成员（1.7类型不再声明原名时才查）
    static Dictionary<string, List<string[]>> _param; // Harmony 参数名 -> 位置映射
    static readonly Dictionary<string, bool> _memberCache = new Dictionary<string, bool>(StringComparer.Ordinal);
    static Dictionary<string, string> _unanimous;     // 真成员 -> 混淆成员，但仅当所有出现都一致；用作「从 mod 自己的元数据里认不出基类」的 override 的最后兜底。
    static Dictionary<string, string> _unanimousField;// 真字段 -> 混淆字段，但仅当所有出现都一致；用作「从 mod 自己的元数据里认不出基类」的 override 的最后兜底。
    static AssemblyDefinition _obf;
    static readonly HashSet<string> _obfNames = new HashSet<string>(StringComparer.Ordinal);
    static readonly Dictionary<string, TypeDefinition> _obfByFull = new Dictionary<string, TypeDefinition>(StringComparer.Ordinal);
    static Resolver _res;
    static Hook _hook;
    static string _gameRoot, _managedDir, _tempDir, _logPath;
    static readonly Dictionary<string, string> _patched = new Dictionary<string, string>();
    static int _nSeen, _nRewritten, _nFailed;
    static readonly object _lock = new object();

    //输出日志
    static void L(string s)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(_logPath, DateTime.Now.ToString("HH:mm:ss.fff") + " " + s + "\r\n",
                                   new UTF8Encoding(false));
            }
        }
        catch { }
    }

    //不开游戏对每个dll跑完整改写流水线 写typebridge.rehearse.log
    public static void Rehearse(string gameRoot)
    {
        try
        {
            _gameRoot = gameRoot;
            _managedDir = Path.Combine(gameRoot, "Spaceflight Simulator_Data", "Managed");
            _tempDir = Path.Combine(Path.GetTempPath(), "sfs17rtbridge");
            _logPath = Path.Combine(gameRoot, "typebridge.rehearse.log");
            Directory.CreateDirectory(_tempDir);
            L("================ TypeBridge rehearse (offline) ================");
            L("gameRoot   = " + _gameRoot);

            LoadMaps();

            var asmPath = Path.Combine(_managedDir, "Assembly-CSharp.dll");
            _res = new Resolver();
            _res.AddSearchDirectory(_managedDir);
            _obf = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = _res });
            _res.Current = _obf;
            foreach (var td in _obf.MainModule.GetTypes())
            {
                // Cecil 把嵌套类型写成 "Outer/Inner"，本文件其余部分（RealFull、bridge_map.tsv）用反射形式 "Outer+Inner"；必须按反射形式做键，否则嵌套类型全部解析不到。
                string key = td.FullName.Replace('/', '+');
                _obfNames.Add(key);
                if (!_obfByFull.ContainsKey(key)) _obfByFull[key] = td;
            }
            L("obfuscated Assembly-CSharp: " + _obfNames.Count + " types");

            foreach (var dir in Directory.GetDirectories(Path.Combine(gameRoot, "Mods")))
                foreach (var f in Directory.GetFiles(dir, "*.dll"))
                {
                    try { Rewrite(f); }
                    catch (Exception e) { L("REHEARSE FAILED " + f + ": " + e.GetType().Name + ": " + e.Message); }
                }
            L("================ rehearse done ================");
        }
        catch (Exception e)
        {
            L("REHEARSE ERROR: " + e.GetType().FullName + ": " + e.Message);
            L(e.StackTrace);
        }
    }

    //安装
    public static void Install()
    {
        try
        {
            _gameRoot = AppDomain.CurrentDomain.BaseDirectory;
            _managedDir = Path.Combine(_gameRoot, "Spaceflight Simulator_Data", "Managed");
            _tempDir = Path.Combine(Path.GetTempPath(), "sfs17rtbridge");
            _logPath = Path.Combine(_gameRoot, "typebridge.log");
            Directory.CreateDirectory(_tempDir);
            L("================ TypeBridge install ================");
            L("gameRoot   = " + _gameRoot);
            L("managedDir = " + _managedDir);

            LoadMaps();

            var asmPath = Path.Combine(_managedDir, "Assembly-CSharp.dll");
            _res = new Resolver();
            _res.AddSearchDirectory(_managedDir);
            _obf = AssemblyDefinition.ReadAssembly(asmPath, new ReaderParameters { AssemblyResolver = _res });
            _res.Current = _obf;
            foreach (var td in _obf.MainModule.GetTypes())
            {
                // Cecil 把嵌套类型写成 "Outer/Inner"，本文件其余部分（RealFull、bridge_map.tsv）用反射形式 "Outer+Inner"；必须按反射形式做键，否则嵌套类型全部解析不到。
                string key = td.FullName.Replace('/', '+');
                _obfNames.Add(key);
                if (!_obfByFull.ContainsKey(key)) _obfByFull[key] = td;
            }
            L("obfuscated Assembly-CSharp loaded via Cecil: " + new FileInfo(asmPath).Length
              + " bytes, " + _obfNames.Count + " types");

            var mi = typeof(Assembly).GetMethod("LoadFrom", new Type[] { typeof(string) });
            if (mi == null) { L("!! Assembly.LoadFrom(string) not found"); return; }
            _hook = new Hook(mi, new Func<Func<string, Assembly>, string, Assembly>(OnLoadFrom));
            L("hook installed on " + mi);

            // mod 按文件夹名顺序加载，改写副本在临时目录所以 LoadFrom 探测无效，只能靠 AssemblyResolve 解析真正缺失的程序集。
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;
            L("AssemblyResolve handler installed");
        }
        catch (Exception e)
        {
            L("INSTALL FAILED: " + e.GetType().FullName + ": " + e.Message);
            L(e.StackTrace);
        }
    }

    class Resolver : DefaultAssemblyResolver
    {
        public AssemblyDefinition Current;
        public override AssemblyDefinition Resolve(AssemblyNameReference name)
        {
            if (name.Name == "Assembly-CSharp" && Current != null) return Current;
            try { return base.Resolve(name); } catch { return null; }
        }
    }

    //映射表
    static void LoadMaps()
    {
        _type = new Dictionary<string, string>();
        _member = new Dictionary<string, string>();
        var raw = new Dictionary<string, double>();

        foreach (var line in ReadResource("bridge_map").Split(new[] { '\n' }))
        {
            var c = line.TrimEnd(new[] { '\r' }).Split(new[] { '\t' });
            if (c.Length != 2 || c[0].Length == 0) continue;
            string prev;
            if (_type.TryGetValue(c[0], out prev))
            {
                // 两个混淆类型共用一个真名 败者不可达 引用它的mod导致"TypeLoadException: Failure has occurred while loading a type."。
                if (prev != c[1]) L("  !! AMBIGUOUS type key \"" + c[0] + "\": " + prev + " vs " + c[1] + "  (keeping " + prev + ")");
                continue;
            }
            _type[c[0]] = c[1];
        }

        var lines = ReadResource("member_map").Split(new[] { '\n' });
        _memberLow = new Dictionary<string, string>();
        var rawLow = new Dictionary<string, double>();
        for (int i = 1; i < lines.Length; i++)
        {
            var c = lines[i].TrimEnd(new[] { '\r' }).Split(new[] { '\t' });
            if (c.Length < 6) continue;
            double v;
            if (!double.TryParse(c[5], NumberStyles.Float, CultureInfo.InvariantCulture, out v)) continue;
            string leaf = c[4].Split(new[] { '+' }).Last().Split(new[] { '/' }).Last();
            string key = c[3] + "|" + c[0] + "|" + c[4];
            string keyLeaf = c[3] + "|" + c[0] + "|" + leaf;
            //低置信度处理
            if (!rawLow.ContainsKey(key) || rawLow[key] < v) { _memberLow[key] = c[2]; rawLow[key] = v; }
            if (!rawLow.ContainsKey(keyLeaf) || rawLow[keyLeaf] < v) { _memberLow[keyLeaf] = c[2]; rawLow[keyLeaf] = v; }
            if (v < 0.75) continue;                       // 主表只收硬证据
            if (!raw.ContainsKey(key) || raw[key] < v) { _member[key] = c[2]; raw[key] = v; }
            if (!raw.ContainsKey(keyLeaf) || raw[keyLeaf] < v) { _member[keyLeaf] = c[2]; raw[keyLeaf] = v; }
        }

        //人工补齐遗漏项
        try
        {
            var ov = ReadResource("member_overrides").Split(new[] { '\n' });
            for (int i = 1; i < ov.Length; i++)
            {
                var c = ov[i].TrimEnd(new[] { '\r' }).Split(new[] { '\t' });
                if (c.Length < 6) continue;
                _member[c[3] + "|" + c[0] + "|" + c[4]] = c[2];
            }
        }
        catch { }

        // Harmony 参数名 -> 位置映射
        _param = new Dictionary<string, List<string[]>>(StringComparer.Ordinal);
        try
        {
            foreach (var line in ReadResource("param_map").Split(new[] { '\n' }))
            {
                var l = line.TrimEnd(new[] { '\r' });
                if (l.Length == 0) continue;
                int tab = l.IndexOf('\t');
                if (tab <= 0) continue;
                string key = l.Substring(0, tab);
                int lastBar = key.LastIndexOf('|');
                if (lastBar <= 0) continue;
                key = key.Substring(0, lastBar);            // 去掉参数个数
                List<string[]> list;
                if (!_param.TryGetValue(key, out list)) { list = new List<string[]>(); _param[key] = list; }
                list.Add(l.Substring(tab + 1).Split(new[] { ',' }));
            }
        }
        catch (Exception e) { L("param_map unavailable: " + e.Message); }

        // 真成员 -> 混淆成员，但仅当所有出现都一致；用作「从 mod 自己的元数据里认不出基类」的 override 的最后兜底。
        var votes = new Dictionary<string, HashSet<string>>();
        var votesF = new Dictionary<string, HashSet<string>>();
        foreach (var kv in _member)
        {
            var p = kv.Key.Split(new[] { '|' });
            if (p.Length != 3) continue;
            if (p[1] == "method")
            {
                HashSet<string> s;
                if (!votes.TryGetValue(p[2], out s)) { s = new HashSet<string>(); votes[p[2]] = s; }
                s.Add(kv.Value);
            }
            else if (p[1] == "field")
            {
                HashSet<string> s;
                if (!votesF.TryGetValue(p[2], out s)) { s = new HashSet<string>(); votesF[p[2]] = s; }
                s.Add(kv.Value);
            }
        }
        _unanimous = new Dictionary<string, string>();
        foreach (var kv in votes) if (kv.Value.Count == 1) _unanimous[kv.Key] = kv.Value.First();
        _unanimousField = new Dictionary<string, string>();
        foreach (var kv in votesF) if (kv.Value.Count == 1) _unanimousField[kv.Key] = kv.Value.First();

        L(string.Format("maps: {0} types, {1} member keys, {2} unanimous fallbacks",
                        _type.Count, _member.Count, _unanimous.Count));
    }

    static string ReadResource(string logical)
    {
        var asm = Assembly.GetExecutingAssembly();
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith(logical, StringComparison.OrdinalIgnoreCase));
        if (name == null) throw new Exception("embedded resource missing: " + logical);
        using (var s = asm.GetManifestResourceStream(name))
        using (var r = new StreamReader(s, Encoding.UTF8))
            return r.ReadToEnd();
    }

    // 在 Mods/*/ 下找 mod 之间的依赖并加载改写副本；只接管真的存在同名 mod 文件夹的名字，框架程序集不动。
    static Assembly OnAssemblyResolve(object sender, ResolveEventArgs e)
    {
        try
        {
            string simple = new AssemblyName(e.Name).Name;
            var modsRoot = Path.Combine(_gameRoot, "Mods");
            if (!Directory.Exists(modsRoot)) return null;
            foreach (var dir in Directory.GetDirectories(modsRoot))
            {
                var cand = Path.Combine(dir, simple + ".dll");
                if (!File.Exists(cand)) continue;
                string key = Path.GetFullPath(cand);
                string rt;
                if (!_patched.TryGetValue(key, out rt) || !File.Exists(rt))
                {
                    rt = Rewrite(cand);
                    if (rt == null) return null;
                    _patched[key] = rt;
                }
                L("RESOLVE " + simple + " -> " + rt);
                return Assembly.LoadFrom(rt);
            }
        }
        catch (Exception ex) { L("AssemblyResolve failed for " + e.Name + ": " + ex.Message); }
        return null;
    }

    //挂钩
    static Assembly OnLoadFrom(Func<string, Assembly> orig, string path)
    {
        try
        {
            if (path == null || !path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                return orig(path);
            if (path.IndexOf("Mods", StringComparison.OrdinalIgnoreCase) < 0)
                return orig(path);

            _nSeen++;
            // 统一缓存键：加载器和 AssemblyResolve 可能用不同分隔符送来同一个文件，重复改写会在首个镜像已加载时删建临时文件（曾造成 UITools 出现 23 与 42 两个身份）。
            string key = Path.GetFullPath(path);
            string cached;
            if (_patched.TryGetValue(key, out cached) && File.Exists(cached))
                return orig(cached);

            string rewritten = Rewrite(path);
            if (rewritten != null)
            {
                _patched[key] = rewritten;
                _nRewritten++;
                L("LOAD  " + path + "\n      -> " + rewritten);
                var asm = orig(rewritten);
                // 在这里强制枚举类型，失败才能报出类型名；游戏加载器只会给无用的 "Failure has occurred while loading a type."。
                try { asm.GetTypes(); L("      GetTypes OK (" + asm.GetTypes().Length + " types)"); }
                catch (ReflectionTypeLoadException rtle)
                {
                    _nFailed++;
                    L("      !! GetTypes failed on " + Path.GetFileName(path));
                    if (rtle.LoaderExceptions != null)
                        foreach (var le in rtle.LoaderExceptions.Where(x => x != null).Take(6))
                            L("         LoaderException: " + le.GetType().Name + ": " + le.Message);
                }
                catch (Exception e) { L("      !! GetTypes threw " + e.GetType().Name + ": " + e.Message); }
                return asm;
            }
        }
        catch (Exception e)
        {
            _nFailed++;
            L("REWRITE FAILED for " + path + ": " + e.GetType().FullName + ": " + e.Message);
        }
        return orig(path);
    }

    //改写器
    static string Rewrite(string modPath)
    {
        // mod 文件夹可能带同级程序集，Cecil 写元数据时要靠它们解析枚举常量等。
        try { _res.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(modPath))); }
        catch { }

        var mod = AssemblyDefinition.ReadAssembly(modPath, new ReaderParameters
        {
            ReadSymbols = false,
            AssemblyResolver = _res
        });
        int cTypes = 0, cMembers = 0, cOverrides = 0; cStrings = 0;

        //重命名mod自己覆写的方法
        // CLR 按「名字 + 签名」绑定 override，所以 mod 方法必须带上被覆写基类的混淆名；在 mod 自己的元数据里上溯基类链，第一个指向 Assembly-CSharp 的 TypeRef 就是真基类名。
        foreach (var t in mod.MainModule.GetTypes())
        {
            try { cOverrides += FixOverrides(t); }
            catch (Exception e) { L("  override scan failed on " + t.FullName + ": " + e.GetType().Name); }
        }

        //成员引用（IL 操作数）
        foreach (var t in mod.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    var mr = ins.Operand as MethodReference;
                    if (mr is GenericInstanceMethod gim) mr = gim.ElementMethod;
                    if (mr != null && FromGame(mr.DeclaringType))
                    {
                        string obf = ResolveMemberName(RealFull(mr.DeclaringType), "method", mr.Name);
                        if (obf != null && obf != mr.Name) { mr.Name = obf; cMembers++; }
                    }
                    var fr = ins.Operand as FieldReference;
                    if (fr != null && FromGame(fr.DeclaringType))
                    {
                        string obf = ResolveMemberName(RealFull(fr.DeclaringType), "field", fr.Name);
                        if (obf != null && obf != fr.Name) { fr.Name = obf; cMembers++; }
                    }
                }
            }

        //Harmony 目标字符串
        // [HarmonyPatch(typeof(BasicMenu), "OnOpen")] 把目标方法写成字符串，只改 typeof() 会让 Harmony 去找一个已不存在的方法、PatchAll 静默跳过（mod「加载成功」却什么都不做）；必须早于 pass 3 改 TypeRef（真类型名是从那些 TypeRef 上读的），也必须早于 FixStringTargets（下一步就把特性里的原始方法名改掉）。
        cParams = FixPatchParameters(mod);
        cStrings += FixStringTargets(mod);

        //类型引用（分两阶段）
        var todo = new List<KeyValuePair<TypeReference, string>>();
        var seen = new HashSet<TypeReference>();
        foreach (var tr in TypeRefsOf(mod.MainModule))
        {
            if (!FromGame(tr)) continue;
            string obf;
            if (!_type.TryGetValue(RealFull(tr), out obf)) continue;
            if (seen.Add(tr)) todo.Add(new KeyValuePair<TypeReference, string>(tr, obf));
        }
        foreach (var t in mod.MainModule.GetTypes())
            foreach (var ca in t.CustomAttributes)
                foreach (var at in AttrTypes(ca))
                {
                    if (!FromGame(at)) continue;
                    string obf;
                    if (!_type.TryGetValue(RealFull(at), out obf)) continue;
                    if (seen.Add(at)) todo.Add(new KeyValuePair<TypeReference, string>(at, obf));
                }
        foreach (var kv in todo)
        {
            string on = kv.Key.Name, ons = kv.Key.Namespace;
            ApplyObf(kv.Key, kv.Value);
            if (kv.Key.Name != on || kv.Key.Namespace != ons) cTypes++;
        }

       // Assembly.GetTypes() 不解析方法体 token，过期 TypeRef 要等 CLR JIT 到用它的方法才暴露，而且只报没有类型名的 "TypeLoadException: Failure has occurred while loading a type."，所以在这里提前点名。
        AuditRefs(mod, Path.GetFileName(modPath));

        //写入临时目录（原子）
        string sub = Path.Combine(_tempDir, Path.GetFileName(Path.GetDirectoryName(modPath)));
        Directory.CreateDirectory(sub);
        string outPath = Path.Combine(sub, Path.GetFileName(modPath));
        string tmp = outPath + ".tmp";
        mod.Write(tmp);
        if (new FileInfo(tmp).Length < 4096) throw new Exception("produced " + new FileInfo(tmp).Length + " bytes");
        using (AssemblyDefinition.ReadAssembly(tmp)) { }        // 有效性校验；必须 Dispose，否则文件一直被占用
        if (File.Exists(outPath)) File.Delete(outPath);
        File.Move(tmp, outPath);

        //Assembly.LoadFrom 的 LoadFrom 上下文只在同一目录探测依赖，从 %TEMP% 加载改写副本会让带同级程序集的 mod（Multiplayer 带 Lidgren.Network.dll）找不到依赖，所以把同级 dll 一起镜像过去。
        try
        {
            string srcDir = Path.GetDirectoryName(Path.GetFullPath(modPath));
            int copied = 0;
            foreach (var sib in Directory.GetFiles(srcDir, "*.dll"))
            {
                string dst = Path.Combine(sub, Path.GetFileName(sib));
                if (string.Equals(Path.GetFullPath(sib), Path.GetFullPath(outPath), StringComparison.OrdinalIgnoreCase)) continue;
                if (!File.Exists(dst)) { File.Copy(sib, dst); copied++; }
            }
            if (copied > 0) L("      mirrored " + copied + " sibling dll(s) into the load context");
        }
        catch (Exception e) { L("      sibling mirror failed: " + e.Message); }

        L(string.Format("PATCH {0}  (overrides {1}, members {2}, types {3}, strings {4})",
                        Path.GetFileName(modPath), cOverrides, cMembers, cTypes, cStrings));
        return outPath;
    }

    static int cStrings;
    static int cParams;

    //Harmony 参数
    static readonly string[] _patchMethodNames = { "Prefix", "Postfix", "Transpiler", "Finalizer" };

    static bool IsPatchMethod(MethodDefinition m)
    {
        // [HarmonyReversePatch] 桩是替换方法而不是前缀，参数必须保留真实位置；改成 __0 会被 Harmony 当成注入的位置参数、从签名里剔除，之后原始方法就解析不到（"Undefined target method for reverse patch method ..."）。
        foreach (var ca in m.CustomAttributes)
        {
            string n0 = ca.AttributeType.Name;
            if (n0 == "HarmonyReversePatch" || n0 == "HarmonyDelegate") return false;
        }
        if (Array.IndexOf(_patchMethodNames, m.Name) >= 0) return true;
        foreach (var ca in m.CustomAttributes)
        {
            string n = ca.AttributeType.Name;
            if (n == "HarmonyPrefix" || n == "HarmonyPostfix" || n == "HarmonyTranspiler" ||
                n == "HarmonyFinalizer" || n == "HarmonyPrepare" || n == "HarmonyCleanup" ||
                n == "HarmonyTargetMethod") return true;
        }
        return false;
    }

    // name 在原始方法参数表里的下标；方法未知或名字在重载之间有歧义时返回 -1。
    static int ParamIndex(string realType, string realMethod, string name)
    {
        List<string[]> rows;
        if (_param == null || !_param.TryGetValue(realType + "|" + realMethod, out rows)) return -1;
        int found = -1;
        foreach (var r in rows)
            for (int i = 0; i < r.Length; i++)
                if (r[i] == name)
                {
                    if (found >= 0 && found != i) return -1;
                    found = i;
                }
        return found;
    }

    // 把每个 [HarmonyPatch] 前缀/后缀的参数名改写成 Harmony 的位置形式；只动确实是原始方法参数的名字，Harmony 注入参数（__instance、__state、MethodBase、Harmony 等）不受影响。
    static int FixPatchParameters(AssemblyDefinition mod)
    {
        int n = 0;
        foreach (var t in mod.MainModule.GetTypes())
        {
            var attrs = t.CustomAttributes.Where(IsHarmonyPatch).ToList();
            if (attrs.Count == 0) continue;

            string realType = null, realMethod = null;
            foreach (var ca in attrs)
                foreach (var arg in ca.ConstructorArguments)
                {
                    var s = arg.Value as string;
                    if (arg.Value is TypeReference)
                    {
                        if (realType == null) realType = RealFull((TypeReference)arg.Value);
                    }
                    else if (s != null && realMethod == null)
                    {
                        if (_type.ContainsKey(s)) { if (realType == null) realType = s; }
                        else realMethod = s;
                    }
                }
            if (realType == null || realMethod == null) continue;

            foreach (var m in t.Methods)
            {
                if (!IsPatchMethod(m)) continue;
                foreach (var p in m.Parameters)
                {
                    string pn = p.Name;
                    if (string.IsNullOrEmpty(pn) || pn.StartsWith("__")) continue;
                    int idx = ParamIndex(realType, realMethod, pn);
                    if (idx < 0) continue;
                    p.Name = "__" + idx;
                    n++;
                    L("  param " + t.FullName + "." + m.Name + " : \"" + pn + "\" -> __" + idx
                      + "   (target " + realType + "::" + realMethod + ")");
                }
            }
        }
        return n;
    }


    //字符串
    static bool IsHarmonyPatch(CustomAttribute ca)
    {
        var n = ca.AttributeType.FullName;
        return n == "HarmonyLib.HarmonyPatch" || n.EndsWith(".HarmonyPatch") || n == "HarmonyPatch";
    }

    static int FixStringTargets(AssemblyDefinition mod)
    {
        int n = 0;
        // mod 自己声明的名字，绝不改写
        var own = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in mod.MainModule.GetTypes())
        {
            foreach (var m in t.Methods) own.Add(m.Name);
            foreach (var f in t.Fields) own.Add(f.Name);
        }

        foreach (var t in mod.MainModule.GetTypes())
        {
            FixOnProvider(t, t.CustomAttributes, own, ref n);
            foreach (var m in t.Methods) FixOnProvider(m, m.CustomAttributes, own, ref n);
            foreach (var f in t.Fields) FixOnProvider(f, f.CustomAttributes, own, ref n);
            foreach (var p in t.Properties) FixOnProvider(p, p.CustomAttributes, own, ref n);
        }

        // 普通 ldstr：AccessTools.Method(typeof(X), "Y") / AccessTools.Method("Ns.T:Y")；不能见到与方法名相同的字符串就改（裸 "Text" 可能只是 UI 标题），只有被附近 HarmonyLib 调用消费、或带显式 "Type:Method" 形式才动。
        foreach (var t in mod.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                var ins = m.Body.Instructions;
                for (int i = 0; i < ins.Count; i++)
                {
                    if (ins[i].OpCode.Code != Code.Ldstr) continue;
                    var s = ins[i].Operand as string;
                    if (string.IsNullOrEmpty(s)) continue;
                    // 「不动 mod 自己的成员名」这条守卫对标题成立，但被 AccessTools.Field/FieldRefAccess 消费的名字是游戏成员，哪怕 mod 恰好同名（InfoOverload 自己也有 _button 字段）
                    bool fieldish0 = ConsumedByFieldLookup(ins, i);
                    if (own.Contains(s) && !fieldish0) continue;
                    bool explicitForm = s.IndexOf(':') > 0;
                    bool fieldish = fieldish0;
                    if (!explicitForm && !fieldish && !ConsumedByHarmony(ins, i)) continue;
                    string obf = fieldish ? FieldTarget(s) : null;
                    if (obf == null && fieldish)
                    {
                        // AccessTools.Field(typeof(Button), "_button") 会在字符串前压 ldtoken，用那个类型来查，跨类型不唯一的字段名才能解析正确
                        string dt = NearbyDeclaringType(ins, i);
                        if (dt != null)
                        {
                            obf = LookupMember(dt, "field", s);
                            if (obf == null && _unanimousField != null)
                            {
                                string u2;
                                if (_unanimousField.TryGetValue(s, out u2)) obf = u2;
                            }
                        }
                    }
                    if (obf == null) obf = StringTarget(s);
                    if (obf == null && fieldish) obf = FieldTarget(s);
                    if (obf != null && obf != s)
                    {
                        ins[i].Operand = obf;
                        n++;
                        L("  string  \"" + s + "\" -> \"" + obf + "\"");
                    }
                }
            }
        if (n > 0) L("  string targets rewritten: " + n);
        return n;
    }

    // 后面几条指令里是否调用了 HarmonyLib 的方法
    static bool ConsumedByHarmony(IList<Instruction> ins, int at)
    {
        int end = Math.Min(ins.Count - 1, at + 8);
        for (int k = at + 1; k <= end; k++)
        {
            var mr = ins[k].Operand as MethodReference;
            if (mr == null) continue;
            var dt = mr.DeclaringType;
            string ns = dt != null ? dt.Namespace : null;
            string fn = dt != null ? dt.FullName : "";
            if ((ns != null && ns.StartsWith("HarmonyLib")) ||
                fn.StartsWith("HarmonyLib") ||
                mr.Name == "GetType" || mr.Name == "Method" || mr.Name == "Property")
                return true;
        }
        return false;
    }

    // 解析裸成员名，或 "Type:Member" / "Type.Member" 形式
    static string StringTarget(string s)
    {
        if (s.IndexOf(' ') >= 0 || s.IndexOf('\n') >= 0) return null;
        int c = s.IndexOf(':');
        if (c > 0)
        {
            string t = s.Substring(0, c), mem = s.Substring(c + 1);
            string tf;
            if (_type.TryGetValue(t, out tf)) t = tf;
            string obf = LookupMember(t, "method", mem);
            return obf == null ? null : t + ":" + obf;
        }
        string u;
        return _unanimous.TryGetValue(s, out u) ? u : null;
    }

    // 字段名同样按字符串查找（AccessTools.Field(typeof(Button), "_button") / Traverse / FieldRefAccess）；以前只处理了方法名，症状是 "MissingFieldException: Field 'NPCKLPNGDON._button' not found."
    static string FieldTarget(string s)
    {
        if (s.IndexOf(' ') >= 0 || s.IndexOf('\n') >= 0) return null;
        int c = s.IndexOf(':');
        if (c > 0)
        {
            string t = s.Substring(0, c), mem = s.Substring(c + 1);
            string tf;
            if (_type.TryGetValue(t, out tf)) t = tf;
            return LookupMember(t, "field", mem);
        }
        string u;
        return _unanimousField != null && _unanimousField.TryGetValue(s, out u) ? u : null;
    }

    // 字符串字面量前刚压入的游戏类型 token（AccessTools.Field(typeof(X), "y")）。
    static string NearbyDeclaringType(IList<Instruction> ins, int at)
    {
        for (int k = at - 1; k >= 0 && k >= at - 8; k--)
        {
            var tr = ins[k].Operand as TypeReference;
            if (tr == null) continue;
            var el = Strip(tr);
            if (FromGame(el)) return RealFull(el);
        }
        return null;
    }

    // 这个字符串是否被「按名字解析字段」的调用消费？
    static bool ConsumedByFieldLookup(IList<Instruction> ins, int at)
    {
        int end = Math.Min(ins.Count - 1, at + 8);
        for (int k = at + 1; k <= end; k++)
        {
            var mr = ins[k].Operand as MethodReference;
            if (mr == null) continue;
            string n = mr.Name;
            // 用包含而不是相等：mod 常把 AccessTools 包进自己的辅助方法（InfoOverload 调 Extensions::FieldRef，Multiplayer 可能又是别的），名字里带 "Field" 就是按字符串取字段名。
            if (n.IndexOf("Field", StringComparison.Ordinal) >= 0) return true;
        }
        return false;
    }

    static void FixOnProvider(Mono.Cecil.ICustomAttributeProvider prov, IEnumerable<CustomAttribute> attrs,
                              HashSet<string> own, ref int n)
    {
        var list = attrs.Where(IsHarmonyPatch).ToList();
        if (list.Count == 0) return;

        // Harmony 会把同一成员上的多个 [HarmonyPatch] 合并，声明类型可能和方法名不在同一个特性上。
        string realType = null;
        foreach (var ca in list)
            foreach (var a in ca.ConstructorArguments)
                if (a.Value is TypeReference) { realType = RealFull((TypeReference)a.Value); break; }

        foreach (var ca in list)
        {
            var args = ca.ConstructorArguments;
            for (int i = 0; i < args.Count; i++)
            {
                var v = args[i].Value;
                var str = v as string;
                if (str == null)
                {
                    var arr = v as CustomAttributeArgument[];
                    if (arr != null)
                        for (int k = 0; k < arr.Length; k++)
                        {
                            var s2 = arr[k].Value as string;
                            if (s2 == null) continue;
                            string o2 = realType != null ? LookupMember(realType, "method", s2) : null;
                            if (o2 != null && o2 != s2)
                            {
                                arr[k] = new CustomAttributeArgument(arr[k].Type, o2);
                                n++; L("  attr-string \"" + s2 + "\" -> \"" + o2 + "\"  (on " + realType + ")");
                            }
                        }
                    continue;
                }
                //故意不加 own.Contains(str) 守卫：补丁类常声明与目标同名的方法（如 GameManager_IsOnLaunchpad 里 Prefix IsOnLaunchpad），跳过这些字符串会让特性仍指向 1.6 方法名，[HarmonyReversePatch] 桩死于 "Undefined target method for reverse patch method ..."；[HarmonyPatch] 里的字符串永远是目标标识符，不是 mod 自己的成员。

                // [HarmonyPatch("Ns.Type")]：用字符串给出的类型名
                string tf;
                if (_type.TryGetValue(str, out tf)) { args[i] = new CustomAttributeArgument(args[i].Type, tf); n++; L("  attr-type \"" + str + "\" -> \"" + tf + "\""); continue; }

                if (realType == null) continue;
                string obf = LookupMember(realType, "method", str);
                if (obf == null)
                {
                    // MethodType.Getter / Setter 指的是访问器，不是属性本身
                    foreach (var a2 in args)
                        if (a2.Value is int && (int)a2.Value == 1) { obf = LookupMember(realType, "method", "get_" + str); break; }
                    if (obf == null)
                        foreach (var a2 in args)
                            if (a2.Value is int && (int)a2.Value == 2) { obf = LookupMember(realType, "method", "set_" + str); break; }
                }
                if (obf != null && obf != str)
                {
                    args[i] = new CustomAttributeArgument(args[i].Type, obf);
                    n++;
                    L("  attr-string \"" + str + "\" -> \"" + obf + "\"  (on " + realType + ")");
                }
                else if (obf == null && realType != null)
                {
                    // 查不到不一定有问题：Beebyte 保留 Unity 消息名（Awake/LateUpdate/OnOpen 等）等不混淆成员，混淆类型若仍声明该名字，补丁照常绑定。
                    if (ObfHasMember(realType, str)) { }
                    else L("  !! UNMAPPED Harmony target \"" + str + "\" on " + realType);
                }
            }
        }
    }

    // realType 的混淆对应类型是否仍声明这个成员名
    static bool ObfHasMember(string realType, string name)
    {
        string obf;
        if (!_type.TryGetValue(realType, out obf)) obf = realType;    // 名字本身未被混淆
        try
        {
            var t = _obf.MainModule.GetType(obf.Replace('+', '/'));
            if (t == null) return false;
            foreach (var m in t.Methods) if (m.Name == name) return true;
            foreach (var f in t.Fields) if (f.Name == name) return true;
            return false;
        }
        catch { return false; }
    }

    static string LookupMember(string realType, string kind, string realMember)
    {
        string obf;
        if (_member.TryGetValue(realType + "|" + kind + "|" + realMember, out obf)) return obf;
        // member_map.tsv 的声明类型键用 Cecil 形式 "Outer/Inner"，而 bridge_map.tsv 和 RealFull() 用反射形式 "Outer+Inner"；不补这一步，所有嵌套游戏类型（SFS.Parts.Modules.BurnMark+BurnSave::FromSave、ModLoader.Loader+... 等）的成员查找全部落空。
        string alt = AltTypeName(realType);
        if (alt != null && _member.TryGetValue(alt + "|" + kind + "|" + realMember, out obf)) return obf;
        return null;
    }

    static string AltTypeName(string t)
    {
        if (t.IndexOf('+') >= 0) return t.Replace('+', '/');
        if (t.IndexOf('/') >= 0) return t.Replace('/', '+');
        return null;
    }

    // 为成员引用挑混淆名：高置信映射优先 → 1.7 仍声明原名就不动 → 否则回退低置信表（元数据顺序/形状）；漏改被 Beebyte 改过名的成员是硬 MissingFieldException/MissingMethodException，而 mod 加载器逐个 JIT，第一个失败就中断整个加载、所有 mod 静默失效。
    static string ResolveMemberName(string realType, string kind, string name)
    {
        string obf = LookupMember(realType, kind, name);
        if (obf != null) return obf;

        string obfType = realType, o;
        if (_type.TryGetValue(realType, out o)) obfType = o;
        if (ObfHasMemberInChain(obfType, kind, name)) return null;      // 该名字逃过了混淆

        string low;
        if (LookupLow(realType, kind, name, out low) && low != name)
        {
            L("  low-confidence " + kind + " " + realType + "::" + name + " -> " + low
              + "   (1.7 no longer declares the original name)");
            return low;
        }
        return null;
    }

    static bool LookupLow(string realType, string kind, string name, out string obf)
    {
        obf = null;
        if (_memberLow == null) return false;
        if (_memberLow.TryGetValue(realType + "|" + kind + "|" + name, out obf)) return true;
        string alt = AltTypeName(realType);
        if (alt != null && _memberLow.TryGetValue(alt + "|" + kind + "|" + name, out obf)) return true;
        return false;
    }

    static bool ObfHasMemberInChain(string obfFull, string kind, string name)
    {
        string key = obfFull + "|" + kind + "|" + name;
        bool v;
        if (_memberCache.TryGetValue(key, out v)) return v;
        v = false;
        TypeDefinition td;
        if (_obfByFull.TryGetValue(obfFull, out td))
        {
            var cur = td; int g = 0;
            while (cur != null && g++ < 24)
            {
                if (kind == "field") { if (cur.Fields.Any(x => x.Name == name)) { v = true; break; } }
                else { if (cur.Methods.Any(x => x.Name == name)) { v = true; break; } }
                TypeDefinition next = null;
                try { if (cur.BaseType != null) next = cur.BaseType.Resolve(); } catch { }
                if (next == null || next.Module != _obf.MainModule) break;
                cur = next;
            }
        }
        _memberCache[key] = v;
        return v;
    }

    // 报告仍指向 1.7 未声明内容的游戏侧引用：它们全是致命的，用到的方法一被 JIT 就抛 TypeLoadException / MissingFieldException / MissingMethodException，而 mod 加载器逐个 JIT，第一个这种失败就中断整个加载、所有 mod 静默失效。
    static void AuditRefs(AssemblyDefinition mod, string label)
    {
        var badT = new List<string>();
        var seenT = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tr in TypeRefsOf(mod.MainModule))
        {
            if (!FromGame(tr)) continue;
            string full = RealFull(tr);
            if (_obfNames.Count > 0 && !_obfNames.Contains(full) && seenT.Add(full)) badT.Add(full);
        }

        var badM = new List<string>();
        var seenM = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in mod.MainModule.GetTypes())
            foreach (var m in t.Methods)
            {
                if (!m.HasBody) continue;
                foreach (var ins in m.Body.Instructions)
                {
                    var fr = ins.Operand as FieldReference;
                    if (fr != null && FromGame(fr.DeclaringType))
                    {
                        string tn = RealFull(fr.DeclaringType);
                        if (!ObfHasMemberInChain(tn, "field", fr.Name) && seenM.Add(tn + "::" + fr.Name))
                            badM.Add(tn + "::" + fr.Name + "   [field, from " + t.FullName + "." + m.Name + "]");
                    }
                    var mr = ins.Operand as MethodReference;
                    if (mr is GenericInstanceMethod gim) mr = gim.ElementMethod;
                    if (mr != null && FromGame(mr.DeclaringType))
                    {
                        string tn = RealFull(mr.DeclaringType);
                        if (!ObfHasMemberInChain(tn, "method", mr.Name) && seenM.Add(tn + "::" + mr.Name))
                            badM.Add(tn + "::" + mr.Name + "   [method, from " + t.FullName + "." + m.Name + "]");
                    }
                }
            }

        if (badT.Count > 0)
        {
            L("  !! " + badT.Count + " unresolved game type reference(s) in " + label);
            foreach (var b in badT.Take(15)) L("        -> " + b);
        }
        if (badM.Count > 0)
        {
            L("  !! " + badM.Count + " unresolved game member reference(s) in " + label);
            foreach (var b in badM.Take(20)) L("        -> " + b);
        }
    }

    static int FixOverrides(TypeDefinition t)
    {
        if (t.BaseType == null) return 0;
        // 先沿 mod 自己的祖先上溯，取第一个位于 Assembly-CSharp 的祖先；泛型实参跨这一跳带过去，这样 `Obs<GameObject>` 基类才能和 mod 的 `IsEqual(GameObject,GameObject)` 比对。
        var cur = t.BaseType;
        var sub = new Dictionary<int, TypeReference>();
        TypeDefinition gameBaseDef = null;
        string gameBase = null;
        int guard = 0;
        while (cur != null && guard++ < 40)
        {
            var el = Strip(cur);
            if (FromGame(el))
            {
                gameBase = RealFull(el);
                string obfFull = gameBase, o;
                if (_type.TryGetValue(gameBase, out o)) obfFull = o;
                TypeDefinition gd;
                if (_obfByFull.TryGetValue(obfFull, out gd)) gameBaseDef = gd;
                var git = Sub(cur, sub) as GenericInstanceType;
                var nsub = new Dictionary<int, TypeReference>();
                if (git != null && gameBaseDef != null)
                    for (int i = 0; i < gameBaseDef.GenericParameters.Count && i < git.GenericArguments.Count; i++)
                        nsub[i] = git.GenericArguments[i];
                sub = nsub;
                break;
            }
            TypeDefinition rd = null;
            try { rd = cur.Resolve(); } catch { }
            if (rd == null) break;
            var g = Sub(cur, sub) as GenericInstanceType;
            var n2 = new Dictionary<int, TypeReference>();
            if (g != null)
                for (int i = 0; i < rd.GenericParameters.Count && i < g.GenericArguments.Count; i++)
                    n2[i] = g.GenericArguments[i];
            sub = n2;
            cur = rd.BaseType;
        }
        // 注意：只有接口关系、没有游戏基类的类型（如 InfoOverload.VisualsManager : MonoBehaviour, I_GLDrawer）也必须走 FixInterfaces，提前 return 会整个漏掉。
        if (gameBase == null) return FixInterfaces(t);

        // 1.7 真基类链提供的全部虚槽位，用来判断是否真的需要改名、以及改名能不能落到槽上。
        var slots = new HashSet<string>(StringComparer.Ordinal);
        var virtualNames = new HashSet<string>(StringComparer.Ordinal);
        var abstractNames = new HashSet<string>(StringComparer.Ordinal);
        if (gameBaseDef != null)
        {
            var def = gameBaseDef;
            var dsub = sub;
            int g2 = 0;
            while (def != null && g2++ < 40)
            {
                foreach (var bm in def.Methods)
                {
                    if (!bm.IsVirtual && !bm.IsAbstract) continue;
                    slots.Add(bm.Name + "|" + ParamsKey(bm, dsub));
                    virtualNames.Add(bm.Name);
                    if (bm.IsAbstract) abstractNames.Add(bm.Name);
                }
                var bt = def.BaseType;
                if (bt == null) break;
                var bts = Sub(bt, dsub);
                TypeDefinition btd = null;
                try { btd = bts.Resolve(); } catch { }
                if (btd == null) break;
                var git = bts as GenericInstanceType;
                var ns2 = new Dictionary<int, TypeReference>();
                if (git != null)
                    for (int i = 0; i < btd.GenericParameters.Count && i < git.GenericArguments.Count; i++)
                        ns2[i] = git.GenericArguments[i];
                def = btd; dsub = ns2;
            }
        }

        int n = 0;
        foreach (var m in t.Methods)
        {
            if (!m.IsVirtual) continue;
            if (m.IsNewSlot && !m.HasOverrides) continue;      // 不是 override

            string mapped = LookupMember(gameBase, "method", m.Name);
            bool fromFallback = false;
            if (mapped == null)
            {
                string u;
                if (_unanimous.TryGetValue(m.Name, out u) && u != m.Name) { mapped = u; fromFallback = true; }
            }

            string pk = ParamsKey(m, null);

            // 该 override 用原名就能绑定
            // Beebyte 保留 Unity 消息、接口实现等 keep 列表成员，1.7 里不少基类成员仍是真名；改这种名字会毁掉 override，基类成员是抽象时 CLR 直接拒绝加载该类型，正是那句没有信息的 "TypeLoadException: Failure has occurred while loading a type."。
            if (slots.Count > 0 && slots.Contains(m.Name + "|" + pk))
            {
                if (mapped != null && mapped != m.Name)
                    L("  override " + t.FullName + "." + m.Name + " : keeping original name (already a slot on "
                      + gameBase + "; rejected " + mapped + (fromFallback ? ", unanimous fallback" : "") + ")");
                continue;
            }

            // 基类有同名的抽象成员，绝不能改名
            if (mapped != null && mapped != m.Name && abstractNames.Contains(m.Name))
            {
                L("  override " + t.FullName + "." + m.Name + " : keeping original name (abstract member of "
                  + gameBase + " carries that name; rejected " + mapped + ")");
                continue;
            }

            if (mapped != null && mapped != m.Name)
            {
                bool binds = slots.Count == 0 || slots.Contains(mapped + "|" + pk);
                if (!binds && virtualNames.Contains(mapped))
                    L("  ~  override " + t.FullName + "." + m.Name + " -> " + mapped
                      + "   (name exists on " + gameBase + " but the signature differs)");
                else if (!binds)
                    L("  !! override " + t.FullName + "." + m.Name + " -> " + mapped
                      + " : no such slot on " + gameBase + " - override will not bind");
                else if (LookupMember(gameBase, "method", m.Name) != null)
                    L("  override " + t.FullName + "." + m.Name + " -> " + mapped + "   (base " + gameBase + ")");
                else
                    L("  override " + t.FullName + "." + m.Name + " -> " + mapped
                      + "   (unanimous fallback; base " + gameBase + ")");

                m.Name = mapped;
                foreach (var ov in m.Overrides)
                {
                    string o2 = LookupMember(RealFull(ov.DeclaringType), "method", ov.Name);
                    if (o2 != null) ov.Name = o2;
                }
                n++;
            }

            // 显式接口实现 / MethodImpl 目标必须跟随接口成员的混淆名（哪怕 mod 方法自身保留原名），否则 CLR 建不出该类型的方法覆写表："Could not load list of method overrides due to Method not found: void .HBPMOPAHHGA.Draw()"（HBPMOPAHHGA = SFS.UI.I_GLDrawer）。
            foreach (var ov in m.Overrides)
            {
                string ovType = RealFull(ov.DeclaringType);
                string o3 = LookupMember(ovType, "method", ov.Name);
                if (o3 == null) { string u; if (_unanimous.TryGetValue(ov.Name, out u)) o3 = u; }
                if (o3 == null) { string low; if (LookupLow(ovType, "method", ov.Name, out low)) o3 = low; }
                if (o3 == null || o3 == ov.Name) continue;
                string ot = ovType, oo;
                if (_type.TryGetValue(ovType, out oo)) ot = oo;
                TypeDefinition od;
                if (!_obfByFull.TryGetValue(ot, out od)) continue;
                if (!od.Methods.Any(x => x.Name == o3)) continue;      // 必须存在于 1.7 接口上
                L("  impl   " + t.FullName + " : " + ovType + "::" + ov.Name + " -> " + o3);
                ov.Name = o3;
                n++;
            }
        }

        n += FixInterfaces(t);
        return n;
    }

    // 隐式与显式接口实现，以及 MethodImpl 目标；与基类链无关。
    static int FixInterfaces(TypeDefinition t)
    {
        int n = 0;
        // 接口实现
        // 隐式 C# 接口实现是 `virtual final newslot`，会被上面的基类遍历当成「不是 override」跳过；1.7 里接口成员改名后类就不再提供它，CLR 拒绝加载整个类型："Could not load list of method overrides due to Method not found: void .HBPMOPAHHGA.Draw()"（HBPMOPAHHGA = SFS.UI.I_GLDrawer）。
        foreach (var itf in t.Interfaces)
        {
            var iel = Strip(itf.InterfaceType);
            if (!FromGame(iel)) continue;
            string realIface = RealFull(iel), obfIface = realIface, o2;
            if (_type.TryGetValue(realIface, out o2)) obfIface = o2;
            TypeDefinition idef;
            if (!_obfByFull.TryGetValue(obfIface, out idef)) continue;

            foreach (var m in t.Methods)
            {
                if (m.Name.Length == 0 || m.Name[0] == '<') continue;
                string obfIm = LookupMember(realIface, "method", m.Name);
                if (obfIm == null)
                {
                    string u;
                    if (_unanimous.TryGetValue(m.Name, out u)) obfIm = u;
                }
                if (obfIm == null)
                {
                    string low;
                    if (LookupLow(realIface, "method", m.Name, out low)) obfIm = low;
                }
                if (obfIm == null || obfIm == m.Name) continue;
                if (!idef.Methods.Any(x => x.Name == obfIm)) continue;   // 必须存在于 1.7 接口上
                L("  iface  " + t.FullName + "." + m.Name + " -> " + obfIm + "   (implements " + realIface + ")");
                m.Name = obfIm;
                n++;
            }
        }

        // MethodImpl / 显式接口实现目标
        // mod 的方法保留点号名 "I_GLDrawer.Draw"，要改的是它指向的那个方法声明。
        foreach (var m in t.Methods)
            foreach (var ov in m.Overrides)
            {
                string ovType = RealFull(ov.DeclaringType);
                string o3 = LookupMember(ovType, "method", ov.Name);
                if (o3 == null) { string u; if (_unanimous.TryGetValue(ov.Name, out u)) o3 = u; }
                if (o3 == null) { string low; if (LookupLow(ovType, "method", ov.Name, out low)) o3 = low; }
                if (o3 == null || o3 == ov.Name) continue;
                string ot = ovType, oo;
                if (_type.TryGetValue(ovType, out oo)) ot = oo;
                TypeDefinition od;
                if (!_obfByFull.TryGetValue(ot, out od)) continue;
                if (!od.Methods.Any(x => x.Name == o3)) continue;
                L("  impl   " + t.FullName + " : " + ovType + "::" + ov.Name + " -> " + o3);
                ov.Name = o3;
                n++;
            }
        return n;
    }

    //签名键（泛型感知）
    static TypeReference Sub(TypeReference t, Dictionary<int, TypeReference> sub)
    {
        if (t == null) return null;
        var gp = t as GenericParameter;
        if (gp != null)
        {
            TypeReference r;
            if (gp.Type == GenericParameterType.Type && sub != null && sub.TryGetValue(gp.Position, out r)) return r;
            return gp;
        }
        var at = t as ArrayType; if (at != null) return new ArrayType(Sub(at.ElementType, sub));
        var brt = t as ByReferenceType; if (brt != null) return new ByReferenceType(Sub(brt.ElementType, sub));
        var pt = t as PointerType; if (pt != null) return new PointerType(Sub(pt.ElementType, sub));
        var git = t as GenericInstanceType;
        if (git != null)
        {
            var ng = new GenericInstanceType(git.ElementType);
            foreach (var a in git.GenericArguments) ng.GenericArguments.Add(Sub(a, sub));
            return ng;
        }
        return t;
    }

    // 游戏类型两边都写成混淆全名，所以 1.7 基类程序集的槽位和 1.6 编译的 mod 引用可以直接比较。
    static string TypeNameKey(TypeReference el)
    {
        el = Strip(el);
        string full = RealFull(el);
        if (FromGame(el))
        {
            string obf;
            if (_type.TryGetValue(full, out obf)) return "G:" + obf;
            return "G:" + full;
        }
        return "X:" + full;
    }

    static string ShapeKey(TypeReference t, Dictionary<int, TypeReference> sub)
    {
        if (t == null) return "void";
        t = Sub(t, sub);
        var at = t as ArrayType; if (at != null) return ShapeKey(at.ElementType, null) + "[]";
        var brt = t as ByReferenceType; if (brt != null) return ShapeKey(brt.ElementType, null) + "&";
        var pt = t as PointerType; if (pt != null) return ShapeKey(pt.ElementType, null) + "*";
        var git = t as GenericInstanceType;
        if (git != null)
        {
            var sb = new StringBuilder(TypeNameKey(git));
            sb.Append('<');
            for (int i = 0; i < git.GenericArguments.Count; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(ShapeKey(git.GenericArguments[i], null));
            }
            sb.Append('>');
            return sb.ToString();
        }
        var gp = t as GenericParameter;
        if (gp != null) return "!" + gp.Position + (gp.Type == GenericParameterType.Method ? "M" : "T");
        return TypeNameKey(t);
    }

    static string ParamsKey(MethodReference m, Dictionary<int, TypeReference> sub)
    {
        var sb = new StringBuilder();
        sb.Append(m.GenericParameters.Count).Append('|');
        for (int i = 0; i < m.Parameters.Count; i++)
        {
            sb.Append(ShapeKey(m.Parameters[i].ParameterType, sub));
            sb.Append(',');
        }
        return sb.ToString();
    }

    //辅助
    static TypeReference Strip(TypeReference t)
    {
        while (t is TypeSpecification) t = ((TypeSpecification)t).ElementType;
        return t;
    }

    static bool FromGame(TypeReference t)
    {
        if (t == null) return false;
        if (t is GenericParameter) return false;      // "!!0" 不是 Assembly-CSharp 里的类型
        t = Strip(t);
        var s = t.Scope;
        if (s is AssemblyNameReference a) return a.Name == "Assembly-CSharp";
        if (s is ModuleDefinition m) return m.Assembly.Name.Name == "Assembly-CSharp";
        return false;
    }

    static string RealFull(TypeReference t)
    {
        var chain = new List<string>();
        var cur = t;
        while (cur != null) { chain.Insert(0, cur.Name); cur = cur.DeclaringType; }
        // 嵌套类型的 Namespace 为空，命名空间挂在最外层声明者上
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

    static void ApplyObf(TypeReference tr, string obf)
    {
        if (tr.DeclaringType != null)
        {
            int plus = obf.LastIndexOf('+');
            tr.Name = plus >= 0 ? obf.Substring(plus + 1) : obf.Substring(obf.LastIndexOf('.') + 1);
            tr.Namespace = "";
            return;
        }
        int i = obf.IndexOf('+');
        string head = i >= 0 ? obf.Substring(0, i) : obf;
        int j = head.LastIndexOf('.');
        if (j < 0) { tr.Namespace = ""; tr.Name = head; }
        else { tr.Namespace = head.Substring(0, j); tr.Name = head.Substring(j + 1); }
    }

    static IEnumerable<TypeReference> TypeRefsOf(ModuleDefinition mod)
    {
        var seen = new HashSet<TypeReference>();
        // 用 List 当显式栈：Unity 的 Mono mscorlib 转发 Stack<T> 的方式 .NET Framework 拒绝加载，而本文件也要被离线复现工具加载。
        var stack = new List<TypeReference>();
        Action<TypeReference> push = x => { if (x != null) stack.Add(x); };
        foreach (var t in mod.GetTypes())
        {
            push(t.BaseType);
            foreach (var i in t.Interfaces) push(i.InterfaceType);
            foreach (var g in t.GenericParameters) foreach (var c in g.Constraints) push(c.ConstraintType);
            foreach (var f in t.Fields) push(f.FieldType);
            foreach (var p in t.Properties) push(p.PropertyType);
            foreach (var e in t.Events) push(e.EventType);
            foreach (var m in t.Methods)
            {
                push(m.ReturnType);
                foreach (var p in m.Parameters) push(p.ParameterType);
                foreach (var g in m.GenericParameters) foreach (var c in g.Constraints) push(c.ConstraintType);
                // MethodImpl / 显式接口实现单独存一张元数据表、以前从不遍历，其声明类型 TypeRef 留 1.6 名而 Interfaces 列表已是 1.7 名，CLR 建覆写表失败："Could not load list of method overrides due to Method not found: void .HBPMOPAHHGA.Draw()"。
                foreach (var ov in m.Overrides)
                {
                    push(ov.DeclaringType);
                    push(ov.ReturnType);
                    foreach (var p in ov.Parameters) push(p.ParameterType);
                }
                if (m.HasBody)
                {
                    foreach (var v in m.Body.Variables) push(v.VariableType);
                    foreach (var ins in m.Body.Instructions)
                    {
                        var o = ins.Operand;
                        if (o is TypeReference tr) push(tr);
                        else if (o is FieldReference fr) { push(fr.DeclaringType); push(fr.FieldType); }
                        else if (o is MethodReference mr) { push(mr.DeclaringType); push(mr.ReturnType); foreach (var p in mr.Parameters) push(p.ParameterType); }
                    }
                }
            }
            foreach (var ca in t.CustomAttributes) push(ca.AttributeType);
        }
        while (stack.Count > 0)
        {
            int last = stack.Count - 1;
            var raw = stack[last];
            stack.RemoveAt(last);
            // 泛型实参也必须遍历：List<SFS.World.Drag.Surface> 是 TypeSpecification，先 Strip() 会把实参丢掉，`Surface` 就没改名，CLR 之后报 "Could not resolve type ... expected class 'SFS.World.Drag.Surface'"。
            var git = raw as GenericInstanceType;
            if (git != null) foreach (var a in git.GenericArguments) push(a);
            var el = Strip(raw);
            if (el == null || !seen.Add(el)) continue;
            if (el.DeclaringType != null) push(el.DeclaringType);
            yield return el;
        }
    }

    static IEnumerable<TypeReference> AttrTypes(CustomAttribute ca)
    {
        var list = new List<TypeReference>();
        Action<CustomAttributeArgument> walk = null;
        walk = a =>
        {
            if (a.Value is TypeReference) list.Add((TypeReference)a.Value);
            else if (a.Value is CustomAttributeArgument) walk((CustomAttributeArgument)a.Value);
            else if (a.Value is CustomAttributeArgument[]) foreach (var x in (CustomAttributeArgument[])a.Value) walk(x);
        };
        list.Add(ca.AttributeType);
        foreach (var a in ca.ConstructorArguments) walk(a);
        foreach (var f in ca.Fields) walk(f.Argument);
        foreach (var p in ca.Properties) walk(p.Argument);
        return list;
    }
}
