// Patches applied to the game's assemblies for the iOS port.
using Mono.Cecil;
using Mono.Cecil.Cil;

static class Patches {
    // iOS has no Steam client. The stock Steamworks.NET
    // SteamManager quits the app when Steam can't load; make it report "not initialized"
    // instead, which every Steam code path in the game already handles.
    public static bool SteamManagerOffline(ModuleDefinition m) {
        var t = m.GetType("SteamManager");
        if (t == null) return false;
        var awake = t.Methods.First(x => x.Name == "Awake");
        var inst = t.Fields.First(f => f.Name == "s_instance");
        var init = t.Fields.First(f => f.Name == "m_bInitialized");
        var unityObject = m.AssemblyResolver.Resolve(AssemblyNameReference.Parse("UnityEngine.CoreModule"))
                           .MainModule.GetType("UnityEngine.Object");
        var component = unityObject.Module.GetType("UnityEngine.Component");
        var getGo = m.ImportReference(component.Methods.First(x => x.Name == "get_gameObject"));
        var ddol = m.ImportReference(unityObject.Methods.First(x => x.Name == "DontDestroyOnLoad"));
        var body = awake.Body;
        body.Instructions.Clear(); body.ExceptionHandlers.Clear(); body.Variables.Clear();
        var il = body.GetILProcessor();
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Stsfld, inst);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Call, getGo); il.Emit(OpCodes.Call, ddol);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_I4_0); il.Emit(OpCodes.Stfld, init);
        il.Emit(OpCodes.Ret);
        return true;
    }

    // Replace a method body with "return default" (void / false / 0 / null).
    static bool Stub(ModuleDefinition m, string type, params string[] methods) {
        var t = m.GetType(type);
        if (t == null) return false;
        foreach (var name in methods) {
            var md = t.Methods.First(x => x.Name == name);
            var b = md.Body; b.Instructions.Clear(); b.ExceptionHandlers.Clear(); b.Variables.Clear();
            var il = b.GetILProcessor();
            var rt = md.ReturnType;
            if (rt.MetadataType == MetadataType.Void) { }
            else if (rt.IsValueType && rt.MetadataType is MetadataType.Single) il.Emit(OpCodes.Ldc_R4, 0f);
            else if (rt.IsValueType && rt.MetadataType is MetadataType.Double) il.Emit(OpCodes.Ldc_R8, 0.0);
            else if (rt.IsValueType && rt.MetadataType is MetadataType.Int64 or MetadataType.UInt64) il.Emit(OpCodes.Ldc_I8, 0L);
            else if (rt.IsValueType && rt.IsPrimitive) il.Emit(OpCodes.Ldc_I4_0);
            else if (!rt.IsValueType) il.Emit(OpCodes.Ldnull);
            else throw new Exception($"Stub: unsupported return type {rt} for {md.FullName}");
            il.Emit(OpCodes.Ret);
        }
        return true;
    }

    // LIV is the PC mixed-reality capture SDK; its native bridge does not exist on Apple
    // platforms and it P/Invokes into it every frame.
    public static bool LivOff(ModuleDefinition m) =>
        Stub(m, "LIV.SDK.Unity.LIV", "OnEnable", "Update", "get__isReady", "get_isActive", "Activate");

    // The PC build's Zenject was compiled without ENABLE_IL2CPP, so it builds injection
    // delegates with Expression.Compile; under IL2CPP that falls into the (linker-stripped)
    // expression interpreter and throws. Returning null makes each caller use its
    // reflection fallback, which is what an IL2CPP build of Zenject compiles to.
    public static bool ZenjectNoCompiledExpressions(ModuleDefinition m) =>
        Stub(m, "Zenject.Internal.ReflectionInfoTypeInfoConverter",
             "TryCreateFactoryMethodCompiledLambdaExpression", "TryGetSetterAsCompiledExpression",
             "TryCreateActionForMethod");

    // MainSystemInit.Init applies the PC desktop-window settings (1280x720 windowed by default)
    // with Screen.SetResolution / Screen.fullScreen; on a phone the port owns the resolution.
    public static bool NoWindowResolution(ModuleDefinition m) {
        var md = m.GetType("MainSystemInit")?.Methods.FirstOrDefault(x => x.Name == "Init");
        if (md == null) return false;
        var il = md.Body.GetILProcessor();
        int done = 0;
        foreach (var ins in md.Body.Instructions.ToList()) {
            if (ins.OpCode != OpCodes.Call || !(ins.Operand is MethodReference mr) || mr.DeclaringType.FullName != "UnityEngine.Screen") continue;
            if (mr.Name != "SetResolution" && mr.Name != "set_fullScreen") continue;
            // drop the call, keep the stack balanced (branch targets stay on this instruction)
            ins.OpCode = OpCodes.Pop; ins.Operand = null;
            for (int i = 1; i < mr.Parameters.Count; i++) il.InsertAfter(ins, il.Create(OpCodes.Pop));
            done++;
        }
        return done == 2;
    }

    // The game has custom song support that only dev builds switch on (AppStaticSettingsSO.
    // enableCustomLevels): Solo then shows a "Custom Songs" category read from CustomLevels.
    public static bool CustomSongsOn(ModuleDefinition m) {
        var md = m.GetType("SinglePlayerLevelSelectionFlowCoordinator")?.Methods.FirstOrDefault(x => x.Name == "get_enableCustomLevels");
        if (md == null) return false;
        var b = md.Body; b.Instructions.Clear(); b.ExceptionHandlers.Clear(); b.Variables.Clear();
        var il = b.GetILProcessor();
        il.Emit(OpCodes.Ldc_I4_1); il.Emit(OpCodes.Ret);
        return true;
    }

    // CustomLevels is looked up under Application.dataPath, which is inside the read-only app
    // bundle on iOS; use the port's folder instead (Documents, reachable from the Files app).
    public static bool CustomLevelsInDocuments(ModuleDefinition m) {
        var cctor = m.GetType("CustomLevelPathHelper")?.Methods.FirstOrDefault(x => x.Name == ".cctor");
        var target = Port?.GetType("BeatSaberPort.Songs")?.Methods.FirstOrDefault(x => x.Name == "GetBasePath");
        var ins = cctor?.Body.Instructions.FirstOrDefault(i => i.OpCode == OpCodes.Call && i.Operand is MethodReference r && r.Name == "get_dataPath");
        if (ins == null || target == null) return false;
        ins.Operand = m.ImportReference(target);
        return true;
    }

    public static ModuleDefinition Port;   // BeatSaberPort.dll, set by the patch command

    // Replace a game method's body with a call to a static method of the port with the
    // same parameters (instance methods drop 'this').
    static bool Redirect(ModuleDefinition m, string type, string method, string portType, string portMethod) {
        var md = m.GetType(type)?.Methods.FirstOrDefault(x => x.Name == method);
        var target = Port?.GetType(portType)?.Methods.FirstOrDefault(x => x.Name == portMethod);
        if (md == null || target == null) return false;
        var b = md.Body; b.Instructions.Clear(); b.ExceptionHandlers.Clear(); b.Variables.Clear();
        var il = b.GetILProcessor();
        int first = md.HasThis ? 1 : 0;
        for (int i = 0; i < md.Parameters.Count; i++) il.Emit(OpCodes.Ldarg, i + first);
        il.Emit(OpCodes.Call, m.ImportReference(target));
        il.Emit(OpCodes.Ret);
        return true;
    }

    // Custom song audio and covers: see port/src/SongMedia.cs.
    const string SM = "BeatSaberPort.SongMedia";
    public static bool FileUrls(ModuleDefinition m) =>
        Redirect(m, "FileHelpers", "GetEscapedURLForFilePath", SM, "FileUrl");
    public static bool SongAudio(ModuleDefinition m) =>
        Redirect(m, "MediaAsyncLoader", "LoadAudioClipFromFilePathAsync", SM, "LoadAudioClip");

    const string TI = "BeatSaberPort.TouchInput";
    public static bool TouchPoses(ModuleDefinition m) =>
        Redirect(m, "DevicelessVRHelper", "GetNodePose", TI, "GetNodePose");
    public static bool TouchButtons(ModuleDefinition m) =>
        Redirect(m, "VRControllersInputManager", "TriggerValue", TI, "TriggerValue") &&
        Redirect(m, "VRControllersInputManager", "VerticalAxisValue", TI, "VerticalAxisValue") &&
        Redirect(m, "VRControllersInputManager", "MenuButtonDown", TI, "MenuButtonDown") &&
        Redirect(m, "VRControllersInputManager", "MenuButton", TI, "MenuButton");

    // Start the port's own code at app start (before Steam/VR init).
    public static bool PortHook(ModuleDefinition m) {
        var t = m.GetType("PCAppInit");
        var md = t?.Methods.First(x => x.Name == "AppStartAndMultiSceneEditorSetup");
        if (md == null || Port == null) return false;
        var init = m.ImportReference(Port.GetType("BeatSaberPort.PortBootstrap").Methods.First(x => x.Name == "Init"));
        var il = md.Body.GetILProcessor();
        il.InsertBefore(md.Body.Instructions[0], il.Create(OpCodes.Call, init));
        return true;
    }

    public static readonly (string dll, string name, Func<ModuleDefinition, bool> fn)[] All = {
        ("Main.dll", "PortHook", PortHook),
        ("Main.dll", "SteamManagerOffline", SteamManagerOffline),
        ("Main.dll", "NoWindowResolution", NoWindowResolution),
        ("Main.dll", "CustomSongsOn", CustomSongsOn),
        ("Main.dll", "CustomLevelsInDocuments", CustomLevelsInDocuments),
        ("Main.dll", "SongAudio", SongAudio),
        ("LIV.dll", "LivOff", LivOff),
        ("HMLib.dll", "TouchPoses", TouchPoses),
        ("HMLib.dll", "TouchButtons", TouchButtons),
        ("HMLib.dll", "FileUrls", FileUrls),
        ("Zenject.dll", "ZenjectNoCompiledExpressions", ZenjectNoCompiledExpressions),
    };
}
