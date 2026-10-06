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

    /// <summary>True when the top <paramref name="count"/> values are all unboxed numbers.</summary>
    public bool TopAreNumbers(int count)
    {
        for (int i = Count - count; i < Count; i++)
            if (!_values[i].IsUnboxedNumber) return false;
        return true;
    }

    /// <summary>Pops the top <paramref name="count"/> numbers into <paramref name="into"/>, first pushed first.</summary>
    public void PopNumbers(double[] into, int count)
    {
        int start = Count - count;
        for (int i = 0; i < count; i++)
        {
            into[i] = _values[start + i].Number;
            _values[start + i] = default;
        }
        Count = start;
    }

    public void Clear()
    {
        Array.Clear(_values, 0, Count);
        Count = 0;
    }
}
