namespace Genesis.Runtime.Scripting
{
    /// <summary>Optional file logging for VM diagnostics (no-op unless wired by the host).</summary>
    public static class AllLogsFileLogger
    {
        public static void WriteLine(string line) { }
        public static void Flush() { }
    }
}
