#nullable disable
using System;
using System.Collections.Generic;
using System.IO;

namespace Genesis.Runtime.Scripting.VM;

public class PgslCompiler
{
    private readonly IReadOnlyDictionary<string, int> _nativeIdMap;

    public PgslCompiler(IReadOnlyDictionary<string, int> nativeIdMap = null)
    {
        _nativeIdMap = nativeIdMap;
    }

    public (List<Instruction> instructions, List<object> constants, Dictionary<string, UserFunction> userFunctions) Compile(string source)
    {
        var lexer = new PgslLexer(source);
        var tokens = lexer.Tokenize();

        var parser = new PgslParser(tokens, _nativeIdMap);
        var (instructions, constants, userFunctions) = parser.Parse();

        return (instructions, constants, userFunctions);
    }

    public (List<Instruction> instructions, List<object> constants, Dictionary<string, UserFunction> userFunctions) CompileFile(string filePath)
    {
        string source = File.ReadAllText(filePath);
        return Compile(source);
    }

    public string Disassemble(List<Instruction> instructions, List<object> constants)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Constants:");
        for (int i = 0; i < constants.Count; i++)
        {
            sb.AppendLine($"  [{i}] {constants[i]}");
        }
        sb.AppendLine();
        sb.AppendLine("Instructions:");
        for (int i = 0; i < instructions.Count; i++)
        {
            sb.AppendLine($"  [{i}] {instructions[i]}");
        }
        return sb.ToString();
    }
}
