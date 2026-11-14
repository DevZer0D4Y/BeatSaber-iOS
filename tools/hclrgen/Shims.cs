// Stand-ins for the few Unity editor types HybridCLR's generator sources use.
using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public static class Debug
    {
        public static void Log(object o) => Console.WriteLine(o);
        public static void LogFormat(string f, params object[] a) => Console.WriteLine(f, a);
        public static void LogWarning(object o) => Console.WriteLine("warning: " + o);
        public static void LogWarningFormat(string f, params object[] a) => Console.WriteLine("warning: " + string.Format(f, a));
        public static void LogError(object o) => Console.Error.WriteLine("error: " + o);
        public static void LogErrorFormat(string f, params object[] a) => Console.Error.WriteLine("error: " + string.Format(f, a));
        public static void Assert(bool c) { if (!c) throw new Exception("assertion failed"); }
        public static void Assert(bool c, string m) { if (!c) throw new Exception("assertion failed: " + m); }
    }
}

namespace UnityEditor
{
    public enum BuildTarget { iOS, StandaloneOSX }
}

namespace HybridCLR.Editor
{
    public static class SettingsUtil
    {
        public static string AotDir, HotDir;
        public static List<string> HotUpdateAssemblyNamesIncludePreserved = new List<string>();
        public static List<string> HotUpdateAssemblyNamesExcludePreserved => HotUpdateAssemblyNamesIncludePreserved;
        public static string GetAssembliesPostIl2CppStripDir(UnityEditor.BuildTarget t) => AotDir;
        public static string GetHotUpdateDllsOutputDirByTarget(UnityEditor.BuildTarget t) => HotDir;
        public static string HybridCLRDataPathInPackage => "";
    }
}

namespace HybridCLR.Editor.Settings
{
    public class HybridCLRSettings
    {
        public static HybridCLRSettings Instance { get; } = new HybridCLRSettings();
        public string[] externalHotUpdateAssembliyDirs = new string[0];
        public int maxMethodBridgeGenericIteration = 10;
    }
}
