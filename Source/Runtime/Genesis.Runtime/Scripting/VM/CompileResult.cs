using System.Collections.Generic;
using Genesis.Runtime.Scripting.VM;

namespace Genesis.Runtime.Scripting.VM;

public sealed class CompileResult
{
    public List<Instruction> Instructions { get; }
    public List<object> Constants { get; }
    public Dictionary<string, UserFunction> UserFunctions { get; }

    public CompileResult(
        List<Instruction> instructions,
        List<object> constants,
        Dictionary<string, UserFunction> userFunctions)
    {
        Instructions = instructions;
        Constants = constants;
        UserFunctions = userFunctions;
    }
}
