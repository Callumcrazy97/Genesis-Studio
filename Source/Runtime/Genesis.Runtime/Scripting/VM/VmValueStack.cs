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
    /// True when the top values are what a typed command call takes: an unboxed number for each
    /// number or bool parameter; text, nothing or an unboxed number (not true or false, whose text
    /// is its own) for each text parameter.
    /// </summary>
    public bool TopMatches(PgslEngineBridge.ArgumentKind[] kinds)
    {
        int start = Count - kinds.Length;
        for (int i = 0; i < kinds.Length; i++)
        {
            ref VmValue value = ref _values[start + i];
            switch (kinds[i])
            {
                case PgslEngineBridge.ArgumentKind.Text:
                    if (!(value.IsString || value.IsNull || (value.IsUnboxedNumber && !value.IsBoolean))) return false;
                    break;
                case PgslEngineBridge.ArgumentKind.Value:
                    break;
                case PgslEngineBridge.ArgumentKind.Integer:
                    // Only what Convert.ToInt32 takes without overflowing; anything else gets the general
                    // path's conversion and its message.
                    if (!value.IsUnboxedNumber || !(value.UnboxedNumber >= int.MinValue && value.UnboxedNumber <= int.MaxValue)) return false;
                    break;
                default:
                    if (!value.IsUnboxedNumber) return false;
                    break;
            }
        }
        return true;
    }

    /// <summary>
    /// Pops the top values into a typed call's arrays (numbers and text), first pushed first. A
    /// number given for text becomes the text it always did (DsMapGet(map, 12) reads key "12"),
    /// without a string made per call for whole numbers.
    /// </summary>
    public void PopArguments(PgslEngineBridge.ArgumentKind[] kinds, double[] numbers, object[] texts)
    {
        int start = Count - kinds.Length;
        for (int i = 0; i < kinds.Length; i++)
        {
            ref VmValue value = ref _values[start + i];
            if (kinds[i] == PgslEngineBridge.ArgumentKind.Text)
                texts[i] = value.IsUnboxedNumber ? PgslNumberText.Of(value.UnboxedNumber) : value.ToObject();
            else if (kinds[i] == PgslEngineBridge.ArgumentKind.Value)
                texts[i] = value.ToObject();
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
