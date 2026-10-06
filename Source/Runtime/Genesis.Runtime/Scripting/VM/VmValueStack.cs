using System;
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>Reusable operand storage; binary instructions replace their two operands in place.</summary>
internal sealed class VmValueStack
{
    private VmValue[] _values = new VmValue[32];
    /// <summary>Values in use. The interpreter's loop keeps its own copy while it runs and sets it back.</summary>
    public int Count { get; internal set; }

    /// <summary>The storage, for the interpreter's loop (valid until the next <see cref="Grow"/>).</summary>
    internal VmValue[] Items => _values;

    /// <summary>Doubles the storage, as <see cref="Push"/> does when full, and returns it.</summary>
    internal VmValue[] Grow()
    {
        Array.Resize(ref _values, checked(_values.Length * 2));
        return _values;
    }

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

    /// <summary>
    /// True when the top values are what a typed command call takes as they are: an unboxed
    /// number for each number or bool parameter, text or nothing for each text parameter.
    /// </summary>
    public bool TopMatches(PgslEngineBridge.ArgumentKind[] kinds)
    {
        int start = Count - kinds.Length;
        for (int i = 0; i < kinds.Length; i++)
        {
            ref VmValue value = ref _values[start + i];
            if (kinds[i] == PgslEngineBridge.ArgumentKind.Text ? !(value.IsString || value.IsNull) : !value.IsUnboxedNumber)
                return false;
        }
        return true;
    }

    /// <summary>Pops the top values into a typed call's arrays (numbers and text), first pushed first.</summary>
    public void PopArguments(PgslEngineBridge.ArgumentKind[] kinds, double[] numbers, object[] texts)
    {
        int start = Count - kinds.Length;
        for (int i = 0; i < kinds.Length; i++)
        {
            ref VmValue value = ref _values[start + i];
            if (kinds[i] == PgslEngineBridge.ArgumentKind.Text) texts[i] = value.ToObject();
            else numbers[i] = value.Number;
            value = default;
        }
        Count = start;
    }

    public void Clear()
    {
        Array.Clear(_values, 0, Count);
        Count = 0;
    }
}
