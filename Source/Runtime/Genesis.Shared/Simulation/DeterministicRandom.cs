using System;
namespace Genesis.Shared.Simulation;

/// <summary>Small, explicitly versioned PRNG whose state is portable across save/load and runtimes.</summary>
public sealed class DeterministicRandom
{
    private uint _state;
    public uint State { get => _state; set => _state = value == 0 ? 0x6D2B79F5u : value; }
    public DeterministicRandom(int seed) => State = unchecked((uint)seed) ^ 0xA511E9B3u;
    public uint NextUInt()
    {
        uint x = _state; x ^= x << 13; x ^= x >> 17; x ^= x << 5; _state = x; return x;
    }
    public double NextDouble() => NextUInt() * (1.0 / 4294967296.0);
    public int Next(int minimum, int exclusiveMaximum)
    {
        if (exclusiveMaximum <= minimum) throw new ArgumentOutOfRangeException(nameof(exclusiveMaximum));
        uint range = checked((uint)((long)exclusiveMaximum - minimum));
        uint threshold = unchecked(0u - range) % range;
        uint value; do { value = NextUInt(); } while (value < threshold);
        return checked((int)((long)minimum + value % range));
    }
}
