#nullable disable
using System;

namespace Genesis.Runtime.Scripting.VM;

public class ReturnException : Exception
{
    public object ReturnValue { get; set; }

    public ReturnException(object returnValue = null)
    {
        ReturnValue = returnValue;
    }
}
