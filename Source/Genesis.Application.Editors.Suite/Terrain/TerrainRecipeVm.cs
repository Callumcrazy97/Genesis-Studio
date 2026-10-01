using System.Globalization;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;
using Genesis.World.Terrain;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Independent PGSL compiler/VM with only numerical terrain natives; no game or file commands.</summary>
/// <remarks>
/// A recipe runs once to read its set-up commands (size, spacing and the whole-terrain steps listed in
/// <see cref="TerrainWorldPlan"/>) and then once per sample to produce a height or a density. Set-up
/// commands do nothing on the per-sample runs.
/// </remarks>
internal sealed class TerrainRecipeVm : IPgslEngineBridge
{
    private readonly TerrainCreationRecipe _recipe;
    private readonly PgslVm _vm;
    private readonly List<Instruction> _instructions;
    private readonly List<object> _constants;
    private readonly CancellationToken _cancel;
    private readonly PgslContext _context = new();
    private readonly DateTime _deadline;
    private readonly bool _collect;
    private bool _setup = true;

    // Functions return a number; commands (from TerrainSize on) return nothing and only act during set-up.
    private static readonly string[] Names =
    [
        "sin", "cos", "abs", "sqrt", "min", "max", "pow", "exp", "floor", "clamp", "noise",
        "fbm", "ridge", "lerp", "smoothstep", "length",
        "TerrainSize", "TerrainSpacing", "TerrainHeights", "Ocean", "Erode", "Rivers", "PaintNatural",
        "Layer", "Scatter", "Sites", "SitePlace", "SitePaint", "ScatterCollision",
    ];
    private const int FirstCommand = 16;

    /// <param name="timeBudget">Wall-clock limit for this VM; a full-size world needs more than a preview.</param>
    /// <param name="collectPlan">False for the extra per-thread VMs of a parallel run: they only sample.</param>
    public TerrainRecipeVm(TerrainCreationRecipe recipe, CancellationToken cancellation,
        TimeSpan? timeBudget = null, bool collectPlan = true)
    {
        _recipe = recipe; _cancel = cancellation; _collect = collectPlan;
        _deadline = DateTime.UtcNow + (timeBudget ?? TimeSpan.FromSeconds(30));
        NativeIdMap = Names.Select((name, i) => (name, i)).ToDictionary(pair => pair.name, pair => pair.i, StringComparer.OrdinalIgnoreCase);
        var compiled = new PgslCompiler(NativeIdMap).Compile(recipe.Code);
        if (compiled.instructions.Count > 4000) throw new InvalidOperationException("Terrain recipes are limited to 4,000 compiled instructions.");
        _instructions = compiled.instructions; _constants = compiled.constants;
        _vm = new PgslVm(this); _vm.LoadUserFunctions(compiled.userFunctions);
        if (_collect) _recipe.World = new TerrainWorldPlan();
        Evaluate(0, 0, 0); _setup = false;
    }
    public float Evaluate(float x, float y, float z)
    {
        _cancel.ThrowIfCancellationRequested();
        if (DateTime.UtcNow > _deadline) throw new InvalidOperationException("Terrain generation ran out of time. Increase spacing or simplify the code.");
        // The compiler emits LOAD_REG for x/y/z, so provide the isolated coordinate context.
        _context.X = x; _context.Y = y; _context.Z = z;
        _vm.SetVariable("u", x / (double)Math.Max(.01f, _recipe.Width) + .5); _vm.SetVariable("v", z / (double)Math.Max(.01f, _recipe.Length) + .5);
        _vm.SetVariable("seed", (double)_recipe.Seed); _vm.SetVariable("height", 0d); _vm.SetVariable("density", (double)y);
        _vm.Execute(_instructions, _constants, clearVariables: false);
        string output = _recipe.Surface == TerrainCodeSurface.Volume ? "density" : "height";
        _vm.TryReadVariable(output, out object value);
        double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (!double.IsFinite(number) || Math.Abs(number) > 1e9) throw new InvalidOperationException($"{output} must be a finite number (at {x:0.##}, {y:0.##}, {z:0.##}).");
        return (float)number;
    }
    public int CommandCount => Names.Length;
    public IReadOnlyDictionary<string, int> NativeIdMap { get; }
    public string NativeName(int id) => Names[id];
    public bool IsNativeVoid(int id) => id >= FirstCommand;
    public bool IsVoid(string name, int argCount) => NativeIdMap.TryGetValue(name, out int id) && id >= FirstCommand;
    public void SetContext(PgslContext context) { }
    public PgslContext GetContext() => _context;
    public IEnumerable<object> FindObjects(string objName) => [];
    public bool TrySetProperty(string name, object value) => false;
    public void BuildNativeCallTable() { }
    public object Invoke(string name, object[] args) => NativeIdMap.TryGetValue(name, out int id)
        ? InvokeNative(id, args)
        : throw new InvalidOperationException($"'{name}' is not available in terrain recipes. Use numerical PGSL and the listed terrain functions.");
    public object InvokeNative(int id, object[] args)
    {
        double A(int index) => index < args.Length ? Convert.ToDouble(args[index], CultureInfo.InvariantCulture) : throw new InvalidOperationException($"Missing argument for {Names[id]}.");
        double O(int index, double fallback) => index < args.Length ? Convert.ToDouble(args[index], CultureInfo.InvariantCulture) : fallback;
        string S(int index) => index < args.Length ? Convert.ToString(args[index], CultureInfo.InvariantCulture) ?? "" : "";
        switch (id)
        {
            case 0: return Math.Sin(A(0));
            case 1: return Math.Cos(A(0));
            case 2: return Math.Abs(A(0));
            case 3: return Math.Sqrt(A(0));
            case 4: return Math.Min(A(0), A(1));
            case 5: return Math.Max(A(0), A(1));
            case 6: return Math.Pow(A(0), A(1));
            case 7: return Math.Exp(A(0));
            case 8: return Math.Floor(A(0));
            case 9: return Math.Clamp(A(0), A(1), A(2));
            case 10: return Noise(A(0), A(1), 0);
            case 11: return Fbm(A(0), A(1), (int)O(2, 5), ridged: false);
            case 12: return Fbm(A(0), A(1), (int)O(2, 5), ridged: true);
            case 13: { double a = A(0); return a + (A(1) - a) * A(2); }
            case 14:
            {
                double low = A(0), high = A(1);
                double t = Math.Abs(high - low) < 1e-12 ? (A(2) < low ? 0 : 1) : Math.Clamp((A(2) - low) / (high - low), 0, 1);
                return t * t * (3 - 2 * t);
            }
            case 15: return Math.Sqrt(A(0) * A(0) + A(1) * A(1));
        }

        _cancel.ThrowIfCancellationRequested();
        if (!_setup) return 0d;
        TerrainWorldPlan? plan = _collect ? _recipe.World : null;
        switch (id)
        {
            case 16: _recipe.Width = (float)A(0); _recipe.Length = (float)A(1); break;
            case 17: _recipe.Spacing = (float)A(0); break;
            case 18: _recipe.MinHeight = (float)A(0); _recipe.MaxHeight = (float)A(1); break;
            case 19: if (plan != null) plan.SeaLevel = (float)O(0, 0); break;
            case 20: if (plan != null) plan.ErosionStrength = (float)Math.Clamp(O(0, 0.5), 0, 2); break;
            case 21: if (plan != null) { plan.RiverCount = (int)Math.Clamp(A(0), 0, 64); plan.RiverBasinSquareKilometres = (float)Math.Clamp(O(1, 1.5), 0.05, 500); } break;
            case 22: if (plan != null) plan.Paint = new TerrainWorldPaint((float)A(0), (float)A(1), (float)O(2, 34)); break;
            case 23:
                if (plan != null)
                {
                    int slot = (int)Math.Clamp(A(0), 1, 4);
                    plan.Layers[slot - 1] = new TerrainWorldLayer(S(1), S(2), (float)Math.Clamp(O(3, 6), 0.25, 4096));
                }
                break;
            case 24:
                if (plan != null)
                {
                    int slot = (int)Math.Clamp(O(5, 0), 0, 4);
                    plan.Scatter.Add(new TerrainScatterLayer
                    {
                        Name = S(0), Model = S(0), DensityPerHectare = (float)A(1),
                        MinimumHeight = (float)O(2, -100000), MaximumHeight = (float)O(3, 100000),
                        MaximumSlopeDegrees = (float)O(4, 30),
                        PaintLayerMask = slot == 0 ? 15 : 1 << (slot - 1),
                        MinimumSpacing = (float)O(6, 3), ClumpSize = (float)O(7, 0),
                        DrawDistance = (float)Math.Max(0, O(8, 0)),
                        Seed = _recipe.Seed + plan.Scatter.Count * 7919,
                    });
                }
                break;
            case 25:
                plan?.Sites.Add(new TerrainWorldSites
                {
                    Count = (int)Math.Clamp(A(0), 0, 256), MinimumHeight = (float)A(1), MaximumHeight = (float)A(2),
                    Radius = (float)Math.Clamp(A(3), 4, 2000), Separation = (float)Math.Max(0, O(4, 0)),
                });
                break;
            case 26:
                if (plan != null)
                {
                    if (plan.Sites.Count == 0) throw new InvalidOperationException("SitePlace needs a Sites(...) command before it.");
                    plan.Sites[^1].Placements.Add(new TerrainWorldPlacement(S(0), (int)Math.Clamp(A(1), 0, 4096),
                        (float)Math.Max(0, O(2, 0)), (float)Math.Max(0, O(3, 1e9)), (float)Math.Max(0.5, O(4, 8))));
                }
                break;
            case 27:
                if (plan != null)
                {
                    if (plan.Sites.Count == 0) throw new InvalidOperationException("SitePaint needs a Sites(...) command before it.");
                    plan.Sites[^1].PaintSlot = (int)Math.Clamp(A(0), 0, 4);
                }
                break;
            case 28:
                if (plan != null)
                {
                    if (plan.Scatter.Count == 0) throw new InvalidOperationException("ScatterCollision needs a Scatter(...) command before it.");
                    plan.Scatter[^1].CollisionRadius = (float)Math.Clamp(A(0), 0, 100);
                    plan.Scatter[^1].CollisionHeight = (float)Math.Clamp(O(1, 4), 0, 500);
                }
                break;
            default:
                throw new InvalidOperationException($"'{Names[id]}' is not available in terrain recipes.");
        }

        return 0d;
    }

    private double Noise(double x, double z, int salt)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        double tx = x - ix, tz = z - iz; tx *= tx * (3 - 2 * tx); tz *= tz * (3 - 2 * tz);
        int seed = _recipe.Seed + salt * 1013;
        double Hash(int a, int b) { uint h = unchecked((uint)(a * 374761393 + b * 668265263 + seed * 69069)); h = (h ^ (h >> 13)) * 1274126177u; return (h ^ (h >> 16)) / (double)uint.MaxValue * 2 - 1; }
        double p = Hash(ix, iz) * (1 - tx) + Hash(ix + 1, iz) * tx;
        double q = Hash(ix, iz + 1) * (1 - tx) + Hash(ix + 1, iz + 1) * tx;
        return p * (1 - tz) + q * tz;
    }

    /// <summary>Layered noise. Plain: -1..1 rolling relief. Ridged: 0..1 with sharp crests.</summary>
    private double Fbm(double x, double z, int octaves, bool ridged)
    {
        octaves = Math.Clamp(octaves, 1, 12);
        double sum = 0, amplitude = 1, total = 0, weight = 1;
        for (int octave = 0; octave < octaves; octave++)
        {
            double value = Noise(x, z, octave + 1);
            if (ridged)
            {
                value = 1 - Math.Abs(value);
                value *= value * weight;
                weight = Math.Clamp(value * 2, 0, 1); // crests carry the finer detail
            }

            sum += value * amplitude; total += amplitude;
            amplitude *= 0.5; x = x * 2.03 + 17.1; z = z * 2.03 - 9.7;
        }

        return sum / total;
    }
}
