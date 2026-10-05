// 从 1.6 未混淆的 Assembly-CSharp.dll 全量导出方法参数名，生成 param_map。
// 输出格式：真类型|真方法|参数个数 <TAB> 参数名1,参数名2,...
// 用法: GenParamMap <Assembly-CSharp.dll> <输出.tsv>
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Mono.Cecil;

class GenParamMap
{
    static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length < 2)
        {
            Console.WriteLine("用法: GenParamMap <Assembly-CSharp.dll> <输出.tsv>");
            return 2;
        }
        string src = args[0], outPath = args[1];
        var res = new DefaultAssemblyResolver();
        res.AddSearchDirectory(Path.GetDirectoryName(Path.GetFullPath(src)));
        var asm = AssemblyDefinition.ReadAssembly(src,
            new ReaderParameters { AssemblyResolver = res });

        int lines = 0, methods = 0;
        using (var w = new StreamWriter(outPath, false, new UTF8Encoding(false)))
        {
            foreach (var t in asm.MainModule.GetTypes())
            {
                // 键用反射形式（Outer+Inner），与 bridge_map / 模组的 HarmonyPatch typeof() 一致
                string typeName = t.FullName.Replace('/', '+');
                if (string.IsNullOrEmpty(typeName)) continue;
                foreach (var m in t.Methods)
                {
                    methods++;
                    if (!m.HasParameters || m.Parameters.Count == 0) continue;
                    var names = new List<string>();
                    foreach (var p in m.Parameters)
                        names.Add(string.IsNullOrEmpty(p.Name) ? ("__p" + p.Index) : p.Name);
                    // 跳过方法名前缀 get_/set_ 之外的原样保留；桥按"属性访问器"也走同一张表
                    w.Write(typeName); w.Write('|'); w.Write(m.Name); w.Write('|');
                    w.Write(names.Count); w.Write('\t');
                    w.Write(string.Join(",", names.ToArray())); w.Write('\n');
                    lines++;
                }
            }
        }
        Console.WriteLine("扫描方法 " + methods + " 个，导出带参数的方法 " + lines + " 行");
        Console.WriteLine("输出: " + outPath);
        return 0;
    }
}
