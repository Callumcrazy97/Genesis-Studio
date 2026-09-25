namespace Genesis.Runtime.Scripting
{
    /// <summary>VM debug flags (mirrors GameSettings debugging fields used by the Shadow VM).</summary>
    public static class ScriptingDebugSettings
    {
        public static bool DebuggingEnabled { get; set; }
        public static bool DebugRuntime1DPgslVm { get; set; } = true;
        public static bool DebugRuntime1DShadowTrace { get; set; }
        public static bool DebugRuntime2DDraw { get; set; } = true;
        public static bool DebugRuntime3DDraw { get; set; } = true;
        public static bool DebugRuntime3DPipeline { get; set; } = true;
        public static bool DebugEditor1DPgslVm { get; set; } = true;
        public static bool DebugEditor1DShadowTrace { get; set; }
        public static bool DebugEditor2DDraw { get; set; } = true;
        public static bool DebugEditor3DDraw { get; set; } = true;
        public static bool DebugEditor3DPipeline { get; set; } = true;
        public static bool VmBytecodeCacheSha256 { get; set; } = true;
        public static bool VmUseRegisterFile { get; set; } = true;
        public static bool UsePgCollections { get; set; } = true;
        public static bool EnableShadowVM { get; set; } = true;
    }
}
