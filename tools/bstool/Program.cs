// bstool: applies the port's patches (Patches.cs) to the game's managed assemblies.
//   bstool patch <Managed dir> <out dir> <BeatSaberPort.dll>
// The input folder is never modified; only the assemblies that change are written to <out dir>,
// together with a copy of BeatSaberPort.dll.
using Mono.Cecil;

static class P {
    public static string ExtraSearchDir;

    static ModuleDefinition Load(string path, string dir) {
        var r = new DefaultAssemblyResolver(); r.AddSearchDirectory(dir);
        if (ExtraSearchDir != null) r.AddSearchDirectory(ExtraSearchDir);
        return ModuleDefinition.ReadModule(path, new ReaderParameters { AssemblyResolver = r });
    }

    static int Main(string[] a) {
        if (a.Length < 4 || a[0] != "patch") {
            Console.Error.WriteLine("usage: bstool patch <Managed dir> <out dir> <BeatSaberPort.dll>");
            return 1;
        }
        Directory.CreateDirectory(a[2]);
        ExtraSearchDir = Path.GetDirectoryName(Path.GetFullPath(a[3]));
        Patches.Port = Load(a[3], a[1]);
        File.Copy(a[3], Path.Combine(a[2], "BeatSaberPort.dll"), true);
        int missing = 0;
        foreach (var g in Patches.All.GroupBy(x => x.dll)) {
            var m = Load(Path.Combine(a[1], g.Key), a[1]);
            foreach (var p in g) {
                bool ok = p.fn(m);
                if (!ok) missing++;
                Console.WriteLine($"{g.Key}: {p.name} -> {(ok ? "applied" : "NOT FOUND")}");
            }
            m.Write(Path.Combine(a[2], g.Key));
        }
        if (missing > 0) Console.Error.WriteLine($"{missing} patch(es) did not apply: is this Beat Saber 1.29.1?");
        return missing == 0 ? 0 : 2;
    }
}
