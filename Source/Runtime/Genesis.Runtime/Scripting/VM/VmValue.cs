#nullable disable
using System;
using System.Runtime.CompilerServices;

namespace Genesis.Runtime.Scripting.VM;

/// <summary>Unboxed bytecode VM values, retaining legacy types at native and debugging boundaries.</summary>
internal readonly struct VmValue
{
    // Two machine words keep delegate returns and stack copies small. Private marker objects
    // distinguish unboxed primitive types without consuming a third word per stack/local slot.
    private sealed class Marker(byte kind) { public readonly byte Kind = kind; }
    private static readonly Marker DoubleMarker = new(0), IntegerMarker = new(1), BooleanMarker = new(2);
    private readonly double _number;
    private readonly object _reference;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private VmValue(double number, object reference)
    { _number = number; _reference = reference; }

    public bool IsNull => _reference == null;
    public bool IsString => _reference is string;
    public bool IsBoolean => ReferenceEquals(_reference, BooleanMarker);
    public bool IsNumeric => (_reference is Marker marker && marker.Kind < 2)
        || _reference is float or long or byte or short or decimal or uint or ulong or ushort or sbyte;
    public double Number { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _reference is Marker ? _number : Convert.ToDouble(_reference); }
    // Preserve legacy PGSL truthiness, including object/string values and non-double boxed numerics.
    public bool Truth { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => _reference is Marker ? _number != 0 : _reference != null; }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static VmValue FromObject(object value) => value switch
    {
        null => default, double number => number, int integer => integer, bool boolean => boolean,
        _ => new(0, value)
    };
    public object ToObject()
    {
        if (ReferenceEquals(_reference, DoubleMarker)) return _number;
        if (ReferenceEquals(_reference, IntegerMarker)) return (int)_number;
        if (IsBoolean) return _number != 0;
        return _reference;
    }
    public override string ToString()
    {
        // Text the same on every PC: a decimal point whatever the locale, and true/false as written.
        if (ReferenceEquals(_reference, DoubleMarker)) return _number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (ReferenceEquals(_reference, IntegerMarker)) return ((int)_number).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (IsBoolean) return _number != 0 ? "true" : "false";
        return _reference?.ToString() ?? "";
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator VmValue(double value) => new(value, DoubleMarker);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator VmValue(int value) => new(value, IntegerMarker);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator VmValue(bool value) => new(value ? 1 : 0, BooleanMarker);
    [MethodImpl(MethodImplOptions.AggressiveInlining)] public static implicit operator VmValue(string value) => new(0, value);
}
