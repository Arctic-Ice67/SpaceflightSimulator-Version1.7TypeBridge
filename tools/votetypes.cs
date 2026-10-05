// votetypes.cs — correct the TYPE map using the MEMBER map.
//
// usage: votetypes.exe <cleanAsm> <obfAsm> <bridge_map.tsv> <member_map.tsv> <out.tsv>
//
// Every member correspondence is also a type correspondence: if
// Builder.CreateSeparator (real) really is AEJOODCDNBB (obf, ilhash 0.95), then
// AEJOODCDNBB's return type IS SFS.UI.ModGUI.Separator, whatever the type map
// claims.  5000 member rows therefore cast ~15000 votes, which is far more
// evidence than the structural type matcher had - and it settles the
// structurally IDENTICAL siblings (Separator vs Space, OnInputStartData vs
// OnInputEndData, ...) that shape matching alone cannot tell apart.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;

static class VoteTypes
{
    static AssemblyDefinition Clean, Obf;
    static readonly Dictionary<string, string> Cur = new Dictionary<string, string>(StringComparer.Ordinal);
    static readonly Dictionary<string, Dictionary<string, int>> Votes =
        new Dictionary<string, Dictionary<string, int>>(StringComparer.Ordinal);

    static string Key(TypeReference t)
    {
        if (t == null) return null;
        if (t is TypeSpecification) return null;                 // arrays/generics: handled by recursion
        return t.FullName.Replace('/', '+');
    }

    static bool InClean(TypeReference t) { return t != null && t.Module == Clean.MainModule; }
    static bool InObf(TypeReference t) { return t != null && t.Module == Obf.MainModule; }

    static void Vote(TypeReference real, TypeReference obf)
    {
        if (real is GenericInstanceType rg && obf is GenericInstanceType og)
        {
            for (int i = 0; i < rg.GenericArguments.Count && i < og.GenericArguments.Count; i++)
                Vote(rg.GenericArguments[i], og.GenericArguments[i]);
            return;
        }
        if (real is ArrayType ra && obf is ArrayType oa) { Vote(ra.ElementType, oa.ElementType); return; }
        string a = Key(real), b = Key(obf);
        if (a == null || b == null) return;
        if (!InClean(real) || !InObf(obf)) return;
        Dictionary<string, int> d;
        if (!Votes.TryGetValue(a, out d)) { d = new Dictionary<string, int>(StringComparer.Ordinal); Votes[a] = d; }
        int n; d.TryGetValue(b, out n); d[b] = n + 1;
    }

    static void Pair(MethodDefinition rm, MethodDefinition om)
    {
        if (rm == null || om == null) return;
        Vote(rm.ReturnType, om.ReturnType);
        if (rm.Parameters.Count != om.Parameters.Count) return;   // signature changed: no vote
        for (int i = 0; i < rm.Parameters.Count; i++)
            Vote(rm.Parameters[i].ParameterType, om.Parameters[i].ParameterType);
    }

    static int Main(string[] a)
    {
        try
        {
            foreach (var line in File.ReadAllLines(a[2], Encoding.UTF8))
            { var c = line.Split('\t'); if (c.Length == 2 && c[0].Length > 0) Cur[c[0]] = c[1]; }
            Clean = AssemblyDefinition.ReadAssembly(a[0]);
            Obf = AssemblyDefinition.ReadAssembly(a[1]);

            var lines = File.ReadAllLines(a[3], Encoding.UTF8);
            int used = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                var c = lines[i].Split('\t');
                if (c.Length < 6) continue;
                double conf; if (!double.TryParse(c[5], System.Globalization.NumberStyles.Float,
                                                  System.Globalization.CultureInfo.InvariantCulture, out conf)) continue;
                if (conf < 0.75) continue;
                var rt = Clean.MainModule.GetType(c[3].Replace('+', '/'));
                var ot = Obf.MainModule.GetType(c[1].Replace('+', '/'));
                if (rt == null || ot == null) continue;
                if (c[0] == "method")
                {
                    var rm = rt.Methods.FirstOrDefault(x => x.Name == c[4]);
                    var om = ot.Methods.FirstOrDefault(x => x.Name == c[2]);
                    if (rm == null || om == null) continue;
                    Pair(rm, om); used++;
                }
                else if (c[0] == "field")
                {
                    var rf = rt.Fields.FirstOrDefault(x => x.Name == c[4]);
                    var of = ot.Fields.FirstOrDefault(x => x.Name == c[2]);
                    if (rf == null || of == null) continue;
                    Vote(rf.FieldType, of.FieldType); used++;
                }
            }
            Console.WriteLine("member rows used as votes: " + used + "  (distinct real types voted: " + Votes.Count + ")");

            var fixes = new List<string>();
            int agree = 0, noVote = 0, weak = 0;
            foreach (var kv in Votes)
            {
                var ranked = kv.Value.OrderByDescending(x => x.Value).ToList();
                if (ranked.Count == 0) continue;
                string top = ranked[0].Key; int topN = ranked[0].Value;
                int second = ranked.Count > 1 ? ranked[1].Value : 0;
                string cur;
                if (!Cur.TryGetValue(kv.Key, out cur)) { noVote++; continue; }
                if (cur == top) { agree++; continue; }
                if (topN < 3 || topN < second * 2) { weak++; continue; }
                fixes.Add(kv.Key + "\t" + cur + "\t" + top + "\t" + topN + "\t" + second);
            }
            Console.WriteLine("types: agree " + agree + ", no current mapping " + noVote + ", weak evidence " + weak
                              + ", CORRECTIONS " + fixes.Count);
            foreach (var f in fixes.Take(40)) Console.WriteLine("   " + f);

            var outMap = new Dictionary<string, string>(Cur, StringComparer.Ordinal);
            foreach (var f in fixes) { var c = f.Split('\t'); outMap[c[0]] = c[2]; }
            using (var w = new StreamWriter(a[4], false, new UTF8Encoding(false)))
                foreach (var kv in outMap.OrderBy(x => x.Key, StringComparer.Ordinal))
                    w.WriteLine(kv.Key + "\t" + kv.Value);
            Console.WriteLine("wrote " + a[4] + " (" + outMap.Count + " entries)");
        }
        catch (Exception e) { Console.WriteLine("FATAL " + e); return 2; }
        return 0;
    }
}
