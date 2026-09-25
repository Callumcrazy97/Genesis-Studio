#nullable disable
using System.Collections.Generic;

namespace Genesis.Runtime.Scripting.VM;

public class UserFunction
{
    public string Name { get; set; }
    public List<string> Parameters { get; set; }
    public List<Instruction> Bytecode { get; set; }
    public List<object> Constants { get; set; }

    public UserFunction(string name, List<string> parameters)
    {
        Name = name;
        Parameters = parameters;
        Bytecode = new List<Instruction>();
        Constants = new List<object>();
    }
}
