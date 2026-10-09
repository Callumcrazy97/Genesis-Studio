using System;
using System.Runtime.CompilerServices;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

/// <summary>
/// Repeatable noise for generated worlds, textures and motion. Every value is worked out with
/// additions, multiplications and <see cref="Math.Floor(double)"/> on doubles and with integer
/// hashing, never with library functions such as sine whose last bit may differ between machines,
/// so the same arguments give the same number on every PC, renderer and run.
/// </summary>
internal static class PgslNoise
{
    /// <summary>Most octaves a fractal sum takes; more add nothing visible and cost time.</summary>
    public const int MaxOctaves = 16;

    private const ulong PrimeX = 0x9E3779B97F4A7C15UL;
    private const ulong PrimeY = 0xC2B2AE3D27D4EB4FUL;
    private const ulong PrimeZ = 0x165667B19E3779F9UL;
    private const ulong OctaveStep = 0x632BE59BD9B4E019UL;
    private const double UnitFromTop53 = 1.0 / 9007199254740992.0;
    private const double UnitFrom21 = 1.0 / 2097152.0;

    // 2D Perlin noise with unit gradients peaks at sqrt(2) / 2; this stretches it to 1.
    private const double Scale2 = 1.4142135623730951;
    private const double Diagonal = 0.7071067811865476;

    private static readonly double[] Gradient2X = [1, -1, 0, 0, Diagonal, -Diagonal, Diagonal, -Diagonal];
    private static readonly double[] Gradient2Y = [0, 0, 1, -1, Diagonal, Diagonal, -Diagonal, -Diagonal];

    // The twelve cube-edge directions of improved Perlin noise, four repeated to make sixteen.
    private static readonly double[] Gradient3X = [1, -1, 1, -1, 1, -1, 1, -1, 0, 0, 0, 0, 1, -1, 0, 0];
    private static readonly double[] Gradient3Y = [1, 1, -1, -1, 0, 0, 0, 0, 1, -1, 1, -1, 1, 1, -1, -1];
    private static readonly double[] Gradient3Z = [0, 0, 0, 0, 1, 1, -1, -1, 1, 1, -1, -1, 0, 0, 1, -1];

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Mix(ulong h)
    {
        h ^= h >> 30; h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27; h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return h;
    }

    /// <summary>A seed as hash bits: every double gives its own (0.5 and 0.7 differ); -0 is 0.</summary>
    public static ulong SeedHash(double seed) => Mix((ulong)BitConverter.DoubleToInt64Bits(seed + 0.0) ^ 0xD6E8FEB86659FD93UL);

    /// <summary>The seed of one octave of a fractal sum, so octaves do not line up.</summary>
    public static ulong OctaveSeed(ulong seed, int octave) => Mix(seed + (ulong)(octave + 1) * OctaveStep);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(ulong seed, long x, long y) => Mix(seed + (ulong)x * PrimeX + (ulong)y * PrimeY);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ulong Hash(ulong seed, long x, long y, long z) => Mix(seed + (ulong)x * PrimeX + (ulong)y * PrimeY + (ulong)z * PrimeZ);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Fade(double t) => t * t * t * (t * (t * 6 - 15) + 10);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Unit(ulong h) => (h >> 11) * UnitFromTop53;

    // Gradient noise is 0 at every lattice point; each seed shifts the lattice by its own fraction
    // of a cell, so whole-number coordinates (a grid sampled with step 1) still vary.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ShiftX(ulong seed) => (seed & 0x1FFFFF) * UnitFrom21;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ShiftY(ulong seed) => ((seed >> 21) & 0x1FFFFF) * UnitFrom21;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double ShiftZ(ulong seed) => ((seed >> 42) & 0x1FFFFF) * UnitFrom21;

    /// <summary>Smooth gradient (Perlin) noise in -1..1.</summary>
    public static double Gradient2(double x, double y, ulong seed)
    {
        x += ShiftX(seed); y += ShiftY(seed);
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        double fx = Math.Floor(x), fy = Math.Floor(y);
        long ix = (long)fx, iy = (long)fy;
        double dx = x - fx, dy = y - fy;
        double u = Fade(dx), v = Fade(dy);
        ulong h00 = Hash(seed, ix, iy) >> 61, h10 = Hash(seed, ix + 1, iy) >> 61;
        ulong h01 = Hash(seed, ix, iy + 1) >> 61, h11 = Hash(seed, ix + 1, iy + 1) >> 61;
        double n00 = Gradient2X[h00] * dx + Gradient2Y[h00] * dy;
        double n10 = Gradient2X[h10] * (dx - 1) + Gradient2Y[h10] * dy;
        double n01 = Gradient2X[h01] * dx + Gradient2Y[h01] * (dy - 1);
        double n11 = Gradient2X[h11] * (dx - 1) + Gradient2Y[h11] * (dy - 1);
        double nx0 = n00 + u * (n10 - n00);
        double nx1 = n01 + u * (n11 - n01);
        return Math.Clamp((nx0 + v * (nx1 - nx0)) * Scale2, -1, 1);
    }

    /// <summary>Smooth gradient (improved Perlin) noise in -1..1.</summary>
    public static double Gradient3(double x, double y, double z, ulong seed)
    {
        x += ShiftX(seed); y += ShiftY(seed); z += ShiftZ(seed);
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return 0;
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        long ix = (long)fx, iy = (long)fy, iz = (long)fz;
        double dx = x - fx, dy = y - fy, dz = z - fz;
        double u = Fade(dx), v = Fade(dy), w = Fade(dz);
        double n000 = Dot3(Hash(seed, ix, iy, iz), dx, dy, dz);
        double n100 = Dot3(Hash(seed, ix + 1, iy, iz), dx - 1, dy, dz);
        double n010 = Dot3(Hash(seed, ix, iy + 1, iz), dx, dy - 1, dz);
        double n110 = Dot3(Hash(seed, ix + 1, iy + 1, iz), dx - 1, dy - 1, dz);
        double n001 = Dot3(Hash(seed, ix, iy, iz + 1), dx, dy, dz - 1);
        double n101 = Dot3(Hash(seed, ix + 1, iy, iz + 1), dx - 1, dy, dz - 1);
        double n011 = Dot3(Hash(seed, ix, iy + 1, iz + 1), dx, dy - 1, dz - 1);
        double n111 = Dot3(Hash(seed, ix + 1, iy + 1, iz + 1), dx - 1, dy - 1, dz - 1);
        double nx00 = n000 + u * (n100 - n000), nx10 = n010 + u * (n110 - n010);
        double nx01 = n001 + u * (n101 - n001), nx11 = n011 + u * (n111 - n011);
        double nxy0 = nx00 + v * (nx10 - nx00), nxy1 = nx01 + v * (nx11 - nx01);
        return Math.Clamp(nxy0 + w * (nxy1 - nxy0), -1, 1);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double Dot3(ulong hash, double dx, double dy, double dz)
    {
        ulong g = hash >> 60;
        return Gradient3X[g] * dx + Gradient3Y[g] * dy + Gradient3Z[g] * dz;
    }

    /// <summary>Smooth value noise in 0..1; at whole-number points, a repeatable random value per point.</summary>
    public static double Value2(double x, double y, ulong seed)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        double fx = Math.Floor(x), fy = Math.Floor(y);
        long ix = (long)fx, iy = (long)fy;
        double u = Fade(x - fx), v = Fade(y - fy);
        double a = Unit(Hash(seed, ix, iy)), b = Unit(Hash(seed, ix + 1, iy));
        double c = Unit(Hash(seed, ix, iy + 1)), d = Unit(Hash(seed, ix + 1, iy + 1));
        double top = a + u * (b - a), bottom = c + u * (d - c);
        return top + v * (bottom - top);
    }

    /// <summary>Smooth value noise in 0..1; at whole-number points, a repeatable random value per point.</summary>
    public static double Value3(double x, double y, double z, ulong seed)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return 0;
        double fx = Math.Floor(x), fy = Math.Floor(y), fz = Math.Floor(z);
        long ix = (long)fx, iy = (long)fy, iz = (long)fz;
        double u = Fade(x - fx), v = Fade(y - fy), w = Fade(z - fz);
        double c000 = Unit(Hash(seed, ix, iy, iz)), c100 = Unit(Hash(seed, ix + 1, iy, iz));
        double c010 = Unit(Hash(seed, ix, iy + 1, iz)), c110 = Unit(Hash(seed, ix + 1, iy + 1, iz));
        double c001 = Unit(Hash(seed, ix, iy, iz + 1)), c101 = Unit(Hash(seed, ix + 1, iy, iz + 1));
        double c011 = Unit(Hash(seed, ix, iy + 1, iz + 1)), c111 = Unit(Hash(seed, ix + 1, iy + 1, iz + 1));
        double x00 = c000 + u * (c100 - c000), x10 = c010 + u * (c110 - c010);
        double x01 = c001 + u * (c101 - c001), x11 = c011 + u * (c111 - c011);
        double y0 = x00 + v * (x10 - x00), y1 = x01 + v * (x11 - x01);
        return y0 + w * (y1 - y0);
    }

    /// <summary>
    /// The repeatable random number of a whole-number point, 0 (included) to 1 (not included): the
    /// lattice value of <see cref="Value2"/>, so it equals ValueNoise2D(Floor(x), Floor(y), salt).
    /// </summary>
    public static double Lattice2(double x, double y, ulong seed)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) return 0;
        return Unit(Hash(seed, (long)Math.Floor(x), (long)Math.Floor(y)));
    }

    /// <summary>The same for a 3D point: ValueNoise3D(Floor(x), Floor(y), Floor(z), salt).</summary>
    public static double Lattice3(double x, double y, double z, ulong seed)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(z)) return 0;
        return Unit(Hash(seed, (long)Math.Floor(x), (long)Math.Floor(y), (long)Math.Floor(z)));
    }

    /// <summary>Octaves to sum: 1 to <see cref="MaxOctaves"/>; anything else is the nearest of those.</summary>
    public static int OctaveCount(double octaves) =>
        double.IsNaN(octaves) ? 1 : (int)Math.Clamp(Math.Floor(octaves), 1, MaxOctaves);

    /// <summary>The seeds of each octave, worked out once for a whole grid.</summary>
    public static void OctaveSeeds(double seed, Span<ulong> seeds)
    {
        ulong hash = SeedHash(seed);
        for (int octave = 0; octave < seeds.Length; octave++) seeds[octave] = OctaveSeed(hash, octave);
    }

    /// <summary>
    /// Fractal (fBm) sum of 2D gradient noise: each octave <paramref name="lacunarity"/> times the
    /// frequency and <paramref name="gain"/> times the amplitude of the one before, divided by the
    /// sum of the amplitudes so the result stays in -1..1.
    /// </summary>
    public static double Fractal2(double x, double y, ReadOnlySpan<ulong> seeds, double lacunarity, double gain)
    {
        double sum = 0, total = 0, amplitude = 1, frequency = 1;
        for (int octave = 0; octave < seeds.Length; octave++)
        {
            sum += amplitude * Gradient2(x * frequency, y * frequency, seeds[octave]);
            total += Math.Abs(amplitude);
            amplitude *= gain;
            frequency *= lacunarity;
        }
        return Normalise(sum, total);
    }

    /// <summary>Fractal (fBm) sum of 3D gradient noise, as <see cref="Fractal2"/>.</summary>
    public static double Fractal3(double x, double y, double z, ReadOnlySpan<ulong> seeds, double lacunarity, double gain)
    {
        double sum = 0, total = 0, amplitude = 1, frequency = 1;
        for (int octave = 0; octave < seeds.Length; octave++)
        {
            sum += amplitude * Gradient3(x * frequency, y * frequency, z * frequency, seeds[octave]);
            total += Math.Abs(amplitude);
            amplitude *= gain;
            frequency *= lacunarity;
        }
        return Normalise(sum, total);
    }

    private static double Normalise(double sum, double total)
    {
        double value = sum / total;
        return double.IsFinite(value) ? Math.Clamp(value, -1, 1) : 0;
    }
}

public static partial class PgslCommands
{
    #region Noise

    [PgslCommand("Noise2D", "Noise2D(x, y, seed) -> number",
        "Smooth gradient (Perlin) noise, -1 to 1; the same on every machine. Features are about 1 unit across: scale positions (x / 32)", "Noise")]
    public static double Noise2D(double x, double y, double seed) =>
        PgslNoise.Gradient2(x, y, PgslNoise.OctaveSeed(PgslNoise.SeedHash(seed), 0));

    [PgslCommand("Noise3D", "Noise3D(x, y, z, seed) -> number",
        "Smooth 3D gradient (Perlin) noise, -1 to 1; the same on every machine", "Noise")]
    public static double Noise3D(double x, double y, double z, double seed) =>
        PgslNoise.Gradient3(x, y, z, PgslNoise.OctaveSeed(PgslNoise.SeedHash(seed), 0));

    [PgslCommand("ValueNoise2D", "ValueNoise2D(x, y, seed) -> number",
        "Smooth value noise, 0 to 1; at whole-number points a repeatable random value per point", "Noise")]
    public static double ValueNoise2D(double x, double y, double seed) => PgslNoise.Value2(x, y, PgslNoise.SeedHash(seed));

    [PgslCommand("ValueNoise3D", "ValueNoise3D(x, y, z, seed) -> number",
        "Smooth 3D value noise, 0 to 1; at whole-number points a repeatable random value per point", "Noise")]
    public static double ValueNoise3D(double x, double y, double z, double seed) => PgslNoise.Value3(x, y, z, PgslNoise.SeedHash(seed));

    [PgslCommand("Hash2", "Hash2(x, y, salt) -> number",
        "A repeatable random number 0 to 1 (1 never) for a whole-number point (fractions are dropped down): the same on every machine; equals ValueNoise2D(Floor(x), Floor(y), salt)", "Noise")]
    public static double Hash2(double x, double y, double salt) => PgslNoise.Lattice2(x, y, PgslNoise.SeedHash(salt));

    [PgslCommand("Hash3", "Hash3(x, y, z, salt) -> number",
        "A repeatable random number 0 to 1 (1 never) for a whole-number 3D point: the same on every machine; equals ValueNoise3D(Floor(x), Floor(y), Floor(z), salt)", "Noise")]
    public static double Hash3(double x, double y, double z, double salt) => PgslNoise.Lattice3(x, y, z, PgslNoise.SeedHash(salt));

    [PgslCommand("FractalNoise2D", "FractalNoise2D(x, y, seed, octaves, lacunarity, gain) -> number",
        "Octaves of Noise2D summed (fBm), -1 to 1: each octave lacunarity times finer (2) and gain times weaker (0.5); 1 to 16 octaves", "Noise")]
    public static double FractalNoise2D(double x, double y, double seed, double octaves, double lacunarity, double gain)
    {
        Span<ulong> seeds = stackalloc ulong[PgslNoise.OctaveCount(octaves)];
        PgslNoise.OctaveSeeds(seed, seeds);
        return PgslNoise.Fractal2(x, y, seeds, lacunarity, gain);
    }

    [PgslCommand("FractalNoise3D", "FractalNoise3D(x, y, z, seed, octaves, lacunarity, gain) -> number",
        "Octaves of Noise3D summed (fBm), -1 to 1; 1 to 16 octaves", "Noise")]
    public static double FractalNoise3D(double x, double y, double z, double seed, double octaves, double lacunarity, double gain)
    {
        Span<ulong> seeds = stackalloc ulong[PgslNoise.OctaveCount(octaves)];
        PgslNoise.OctaveSeeds(seed, seeds);
        return PgslNoise.Fractal3(x, y, z, seeds, lacunarity, gain);
    }

    [PgslCommand("NoiseFillGrid", "NoiseFillGrid(grid, x0, y0, step, seed, octaves, lacunarity, gain, scale, offset) -> number",
        "Fill a whole DsGrid in one call: cell (i, j) = offset + scale * FractalNoise2D(x0 + i * step, y0 + j * step, ...); returns the cells filled", "Noise")]
    public static double NoiseFillGrid(double grid, double x0, double y0, double step, double seed,
        double octaves, double lacunarity, double gain, double scale, double offset)
    {
        PgslGrid target = Resolve<PgslGrid>("grid", grid);
        if (target is null) return 0;
        Span<ulong> seeds = stackalloc ulong[PgslNoise.OctaveCount(octaves)];
        PgslNoise.OctaveSeeds(seed, seeds);
        int width = target.Width, height = target.Height;
        for (int j = 0; j < height; j++)
        {
            double y = y0 + j * step;
            int row = j * width;
            for (int i = 0; i < width; i++)
                target.Put(row + i, offset + scale * PgslNoise.Fractal2(x0 + i * step, y, seeds, lacunarity, gain));
        }
        return (double)width * height;
    }

    [PgslCommand("NoiseFillGrid3D", "NoiseFillGrid3D(grid, x0, y0, z0, step, plane, seed, octaves, lacunarity, gain, scale, offset) -> number",
        "Fill a DsGrid with a flat slice of FractalNoise3D: plane \"xy\", \"xz\" or \"yz\" names the axes i and j step along from (x0, y0, z0); returns the cells filled", "Noise")]
    public static double NoiseFillGrid3D(double grid, double x0, double y0, double z0, double step, string plane, double seed,
        double octaves, double lacunarity, double gain, double scale, double offset)
    {
        PgslGrid target = Resolve<PgslGrid>("grid", grid);
        int axes = (plane?.Trim().ToLowerInvariant()) switch { "xy" => 0, "xz" => 1, "yz" => 2, _ => -1 };
        if (target is null || axes < 0) return 0;
        Span<ulong> seeds = stackalloc ulong[PgslNoise.OctaveCount(octaves)];
        PgslNoise.OctaveSeeds(seed, seeds);
        int width = target.Width, height = target.Height;
        for (int j = 0; j < height; j++)
        {
            int row = j * width;
            for (int i = 0; i < width; i++)
            {
                // The same sums a script makes for one cell: start + index * step on the stepped axes.
                double x = axes == 2 ? x0 : x0 + i * step;
                double y = axes switch { 0 => y0 + j * step, 1 => y0, _ => y0 + i * step };
                double z = axes == 0 ? z0 : z0 + j * step;
                target.Put(row + i, offset + scale * PgslNoise.Fractal3(x, y, z, seeds, lacunarity, gain));
            }
        }
        return (double)width * height;
    }

    #endregion
}
