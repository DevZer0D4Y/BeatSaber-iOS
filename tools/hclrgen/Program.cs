// hclrgen: generate HybridCLR's MethodBridge.cpp without the Unity editor, the same way its
// "HybridCLR/Generate/MethodBridgeAndReversePInvokeWrapper" command does.
//   hclrgen <aot dll dir> <hot dll dir> <MethodBridge.cpp.tpl> <out MethodBridge.cpp> [hot names, comma-separated]
//   hclrgen order <dll dir> <names file> [late,names]   sort the names so that every assembly comes
//       after the ones it references (the interpreter loads them in order). References to the "late"
//       assemblies are ignored: the patched game calls into BeatSaberPort, which itself references
//       the game; call sites are resolved lazily, so the port can simply load last.
// The bridges come from the method signatures of the AOT (compiled-in) assemblies; the hot
// (interpreted) assemblies only add native-call wrappers for their P/Invokes and calli sites.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using HybridCLR.Editor;
using HybridCLR.Editor.Meta;
using HybridCLR.Editor.MethodBridge;

static class Program
{
    // Depth-first: references first. A cycle can't be ordered; it is reported and broken
    // where it was found.
    static List<string> OrderByReferences(List<string> names, string dir, List<string> late)
    {
        var set = new HashSet<string>(names);
        var refs = new Dictionary<string, List<string>>();
        foreach (var n in names)
        {
            var path = Path.Combine(dir, n + ".dll");
            refs[n] = File.Exists(path)
                ? dnlib.DotNet.ModuleDefMD.Load(path).GetAssemblyRefs().Select(r => r.Name.String).Where(set.Contains).Where(r => r != n && !late.Contains(r)).ToList()
                : new List<string>();
        }
        var order = new List<string>(); var done = new HashSet<string>(); var stack = new List<string>();
        void Visit(string n)
        {
            if (done.Contains(n)) return;
            int i = stack.IndexOf(n);
            if (i >= 0) { Console.WriteLine("cycle: " + string.Join(" -> ", stack.Skip(i).Append(n))); return; }
            stack.Add(n);
            foreach (var r in refs[n]) Visit(r);
            stack.RemoveAt(stack.Count - 1);
            done.Add(n); order.Add(n);
        }
        foreach (var n in names.Where(x => !late.Contains(x))) Visit(n);
        foreach (var n in late.Where(set.Contains)) Visit(n);
        return order;
    }

    static int Main(string[] a)
    {
        if ((a.Length == 3 || a.Length == 4) && a[0] == "order")
        {
            var names = File.ReadAllLines(a[2]).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            var late = a.Length == 4 ? a[3].Split(',').ToList() : new List<string>();
            var sorted = OrderByReferences(names, Path.GetFullPath(a[1]), late);
            File.WriteAllLines(a[2], sorted);
            Console.WriteLine($"hclrgen: {sorted.Count} assemblies in load order");
            return 0;
        }
        if (a.Length < 4) { Console.Error.WriteLine("usage: hclrgen <aot dir> <hot dir> <template> <out> [hot names]"); return 1; }
        SettingsUtil.AotDir = Path.GetFullPath(a[0]);
        SettingsUtil.HotDir = Path.GetFullPath(a[1]);
        var hot = a.Length > 4 && a[4].Length > 0 ? a[4].Split(',').ToList()
            : Directory.GetFiles(SettingsUtil.HotDir, "*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
        SettingsUtil.HotUpdateAssemblyNamesIncludePreserved = hot;

        var aotNames = Directory.GetFiles(SettingsUtil.AotDir, "*.dll").Select(Path.GetFileNameWithoutExtension).ToList();
        var collector = new AssemblyReferenceDeepCollector(new PathAssemblyResolver(SettingsUtil.AotDir), aotNames);
        var analyzer = new Analyzer(new Analyzer.Options { MaxIterationCount = 10, Collector = collector });
        analyzer.Run();

        var resolver = new CombinedAssemblyResolver(new PathAssemblyResolver(SettingsUtil.AotDir), new PathAssemblyResolver(SettingsUtil.HotDir));
        var cache = new AssemblyCache(resolver);
        var reverse = new MonoPInvokeCallbackAnalyzer(cache, hot); reverse.Run();
        var calli = new CalliAnalyzer(cache, hot); calli.Run();
        var pinvoke = new PInvokeAnalyzer(cache, hot); pinvoke.Run();

        var g = new Generator(new Generator.Options
        {
            TemplateCode = File.ReadAllText(a[2], Encoding.UTF8),
            OutputFile = a[3],
            GenericMethods = analyzer.GenericMethods,
            ReversePInvokeMethods = reverse.ReversePInvokeMethods,
            CalliMethodSignatures = calli.CalliMethodSignatures.Concat(pinvoke.PInvokeMethodSignatures).ToList(),
            Development = false,
        });
        g.Generate();
        Console.WriteLine($"hclrgen: {aotNames.Count} AOT + {hot.Count} hot assemblies -> {a[3]}");
        return 0;
    }
}
