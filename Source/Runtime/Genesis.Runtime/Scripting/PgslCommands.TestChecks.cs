using System;
using System.IO;
using Genesis.Runtime.Project;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

public static partial class PgslCommands
{
    /// <summary>The project's Logs/checks.log: one line per TestCheck, "PASS|FAIL&lt;tab&gt;name&lt;tab&gt;detail".</summary>
    public const string TestChecksFileName = "checks.log";

    private static readonly object TestChecksGate = new();

    /// <summary>
    /// A game's self-test: scripts report what they checked while the game runs (a door opened, the
    /// player reached the exit, a count is right). Studio's <c>game.run</c> clears the file before a
    /// run and returns its checks with the report, so an AI or a person can play a scripted run and
    /// read pass or fail rather than guess from pictures.
    /// </summary>
    [PgslCommand("TestCheck", "TestCheck(name, passed, detail) -> bool", "Report a self-test result (pass or fail with a detail); Studio's game.run collects them from the project's Logs/checks.log", "Debug")]
    public static bool TestCheck(string name, object passed, string detail = "")
    {
        bool ok = Truthy(passed);
        string line = $"{(ok ? "PASS" : "FAIL")}\t{Clean(name)}\t{Clean(detail)}";
        try { VMLogger.Log("TestCheck " + line.Replace('\t', ' ')); } catch { }
        string project = ProjectPath;
        if (string.IsNullOrEmpty(project)) return ok;
        try
        {
            lock (TestChecksGate)
            {
                string folder = ProjectPaths.LogsDir(project);
                Directory.CreateDirectory(folder);
                File.AppendAllText(Path.Combine(folder, TestChecksFileName), line + Environment.NewLine);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A check that cannot be written must not stop the game it is checking.
        }
        return ok;
    }

    private static string Clean(string text) => (text ?? string.Empty).Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ').Trim();
}
