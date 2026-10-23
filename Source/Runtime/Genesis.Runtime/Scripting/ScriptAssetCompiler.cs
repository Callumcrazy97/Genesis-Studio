using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting;

public sealed class CompiledScriptAsset
{
    public string Name { get; set; }
    public string SourceHash { get; set; }
    public string ProcessedSource { get; set; }
    public CompileResult CompileResult { get; set; }
}

/// <summary>Compiles PGSL script assets for <see cref="ScriptAssetRegistry"/>.</summary>
public static class ScriptAssetCompiler
{
    private const int LargeFunctionInstructions = 4096;

    public static CompiledScriptAsset Compile(string name, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Script source is empty.", nameof(source));

        string processed;
        using (Genesis.Shared.Diagnostics.LoadProfile.Begin("prepare a Script's text"))
            processed = PgslCommands.PreProcessScript(source, isForVm: true, PgslCommands.ProjectPath);
        string hash;
        using (Genesis.Shared.Diagnostics.LoadProfile.Begin("hash a Script"))
            hash = ComputeSha256(processed);
        CompileResult result;
        using (Genesis.Shared.Diagnostics.LoadProfile.Begin("compile a Script"))
            result = VMEngine.CompilePrepared(processed);
        if (result == null)
        {
            try
            {
                var ast = PgslAstBuilder.Parse(processed);
                if (ast.Body.Count == 0)
                {
                    result = new CompileResult(new List<Instruction>(), new List<object>(), new Dictionary<string, UserFunction>());
                }
            }
            catch { }

            if (result == null)
                throw new InvalidOperationException($"Failed to compile script '{name}'.");
        }

        DecodeLargeFunctions(result);

        return new CompiledScriptAsset
        {
            Name = name,
            SourceHash = hash,
            ProcessedSource = processed,
            CompileResult = result
        };
    }

    /// <summary>
    /// Decodes each large function for the interpreter here, on the thread compiling (or reading) it
    /// while the game starts, not on the game's thread the first time it is called (GenesisCraft's
    /// generated data functions, all called by its first room's Create event).
    /// </summary>
    internal static void DecodeLargeFunctions(CompileResult result)
    {
        if (result?.UserFunctions == null) return;
        foreach (UserFunction function in result.UserFunctions.Values)
            if (function.Bytecode?.Count >= LargeFunctionInstructions) PgslProgram.ForFunction(function);
    }

    public static string ComputeSha256(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(hash);
    }
}
