using System;
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>Reusable operand storage; binary instructions replace their two operands in place.</summary>
internal sealed class VmValueStack
{
    private VmValue[] _values = new VmValue[32];
    public int Count { get; private set; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Push(VmValue value)
    {
        if (Count == _values.Length) Array.Resize(ref _values, checked(Count * 2));
        _values[Count++] = value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VmValue Pop()
    {
        if (Count == 0) throw new InvalidOperationException("Stack is empty.");
        int index = --Count;
        VmValue value = _values[index];
        _values[index] = default;
        return value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public VmValue Peek()
    {
        if (Count == 0) throw new InvalidOperationException("Stack is empty.");
        return _values[Count - 1];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref VmValue CombineTop(out VmValue right)
    {
        if (Count < 2) throw new InvalidOperationException("Stack underflow for binary operation");
        right = _values[--Count];
        _values[Count] = default;
        return ref _values[Count - 1];
    }

    public void Clear()
    {
        Array.Clear(_values, 0, Count);
        Count = 0;
    }
}
