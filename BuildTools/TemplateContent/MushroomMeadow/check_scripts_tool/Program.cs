using System;
using System.IO;
using System.Linq;
using Genesis.Runtime.Scripting;
using Genesis.Runtime.Scripting.VM;

// Usage: CheckScripts <project folder (contains Assets)>   exit code 0 = no errors
internal static class Program
{
    private static int Main(string[] args)
    {
        string root = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        PgslCommands.ProjectPath = root;
        int errors = 0;

        PgslValidationReport report = PgslScriptValidator.ValidateProject(root, strict: true);
        Console.WriteLine("== strict validation: " + report.ScriptCount + " scripts, " + report.Errors.Count + " errors, " + report.Warnings.Count + " warnings");
        foreach (string e in report.Errors) Console.WriteLine("ERROR   " + e);
        foreach (string w in report.Warnings) Console.WriteLine("WARNING " + w);
        errors += report.Errors.Count;

        string assets = Path.Combine(root, "Assets");
        int compiled = 0;
        foreach (string file in Directory.GetFiles(assets, "*.pgsl", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                if (VMEngine.Compile(File.ReadAllText(file)) == null) { Console.WriteLine("ERROR   VM compile produced nothing: " + Path.GetRelativePath(root, file)); errors++; }
                compiled++;
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR   VM compile " + Path.GetRelativePath(root, file) + ": " + ex.Message.Replace("\r", " ").Replace("\n", " | "));
                errors++;
            }
        }
        Console.WriteLine("== VM compile: " + compiled + " files ok, total errors " + errors);
        return errors == 0 ? 0 : 1;
    }
}
