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
    public static CompiledScriptAsset Compile(string name, string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Script source is empty.", nameof(source));

        string processed = PgslCommands.PreProcessScript(source, isForVm: true, PgslCommands.ProjectPath);
        string hash = ComputeSha256(processed);
        var result = VMEngine.Compile(processed);
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

        return new CompiledScriptAsset
        {
            Name = name,
            SourceHash = hash,
            ProcessedSource = processed,
            CompileResult = result
        };
    }

    public static string ComputeSha256(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text ?? ""));
        return Convert.ToHexString(hash);
    }
}
