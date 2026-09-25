using System.Globalization;
using Genesis.Runtime.Scripting.VM;
using Genesis.Shared.Scripting;

namespace Genesis.Application.Editors.Suite.Terrain;

/// <summary>Independent PGSL compiler/VM with only numerical terrain natives; no game or file commands.</summary>
internal sealed class TerrainRecipeVm : IPgslEngineBridge
{
    private readonly TerrainCreationRecipe _recipe;
    private readonly PgslVm _vm;
    private readonly List<Instruction> _instructions;
    private readonly List<object> _constants;
    private readonly CancellationToken _cancel;
    private readonly PgslContext _context = new();
    private readonly DateTime _deadline = DateTime.UtcNow.AddSeconds(30);
    private bool _setup = true;
    private static readonly string[] Names = ["sin", "cos", "abs", "sqrt", "min", "max", "pow", "exp", "floor", "clamp", "noise", "TerrainSize", "TerrainSpacing"];
    public TerrainRecipeVm(TerrainCreationRecipe recipe, CancellationToken cancellation)
    {
        _recipe = recipe; _cancel = cancellation;
        NativeIdMap = Names.Select((name, i) => (name, i)).ToDictionary(pair => pair.name, pair => pair.i, StringComparer.OrdinalIgnoreCase);
        var compiled = new PgslCompiler(NativeIdMap).Compile(recipe.Code);
        if (compiled.instructions.Count > 4000) throw new InvalidOperationException("Terrain recipes are limited to 4,000 compiled instructions.");
        _instructions = compiled.instructions; _constants = compiled.constants;
        _vm = new PgslVm(this); _vm.LoadUserFunctions(compiled.userFunctions);
        Evaluate(0, 0, 0); _setup = false;
    }
    public float Evaluate(float x, float y, float z)
    {
        _cancel.ThrowIfCancellationRequested();
        if (DateTime.UtcNow > _deadline) throw new InvalidOperationException("Terrain generation exceeded 30 seconds. Increase spacing or simplify the code.");
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
    public bool IsNativeVoid(int id) => id >= 11;
    public bool IsVoid(string name, int argCount) => name.StartsWith("Terrain", StringComparison.OrdinalIgnoreCase);
    public void SetContext(PgslContext context) { }
    public PgslContext GetContext() => _context;
    public IEnumerable<object> FindObjects(string objName) => [];
    public bool TrySetProperty(string name, object value) => false;
    public void BuildNativeCallTable() { }
    public object InvokeNative(int id, object[] args) => Invoke(Names[id], args);
    public object Invoke(string name, object[] args)
    {
        _cancel.ThrowIfCancellationRequested();
        double A(int index) => index < args.Length ? Convert.ToDouble(args[index], CultureInfo.InvariantCulture) : throw new InvalidOperationException($"Missing argument for {name}.");
        switch (name.ToLowerInvariant())
        {
            case "terrainsize": if (_setup) { _recipe.Width = (float)A(0); _recipe.Length = (float)A(1); } return 0d;
            case "terrainspacing": if (_setup) _recipe.Spacing = (float)A(0); return 0d;
            case "sin": return Math.Sin(A(0)); case "cos": return Math.Cos(A(0)); case "abs": return Math.Abs(A(0));
            case "sqrt": return Math.Sqrt(A(0)); case "min": return Math.Min(A(0), A(1)); case "max": return Math.Max(A(0), A(1));
            case "pow": return Math.Pow(A(0), A(1)); case "exp": return Math.Exp(A(0)); case "floor": return Math.Floor(A(0));
            case "clamp": return Math.Clamp(A(0), A(1), A(2)); case "noise": return Noise(A(0), A(1));
            default: throw new InvalidOperationException($"'{name}' is not available in terrain recipes. Use numerical PGSL and the listed terrain functions.");
        }
    }
    private double Noise(double x, double z)
    {
        int ix = (int)Math.Floor(x), iz = (int)Math.Floor(z);
        double tx = x - ix, tz = z - iz; tx *= tx * (3 - 2 * tx); tz *= tz * (3 - 2 * tz);
        double Hash(int a, int b) { uint h = unchecked((uint)(a * 374761393 + b * 668265263 + _recipe.Seed * 69069)); h = (h ^ (h >> 13)) * 1274126177u; return (h ^ (h >> 16)) / (double)uint.MaxValue * 2 - 1; }
        double p = Hash(ix, iz) * (1 - tx) + Hash(ix + 1, iz) * tx;
        double q = Hash(ix, iz + 1) * (1 - tx) + Hash(ix + 1, iz + 1) * tx;
        return p * (1 - tz) + q * tz;
    }
}
