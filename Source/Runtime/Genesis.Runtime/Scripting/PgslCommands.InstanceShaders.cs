using System;
using Genesis.Runtime.Rendering;
using Genesis.Shared.ECS;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A Shader resource's values per instance, while the game runs: every weapon on a map can wear its
// own camo, every window its own glow, from one shader. Values are set by the name a parameter has
// in the shader's GenesisParameters and override the shader's (and the Object's) own values.
public static partial class PgslCommands
{
    [PgslCommand("ShaderSet", "ShaderSet(shader)", "Draw this instance with a Shader resource (empty for none)", "Shaders")]
    public static void ShaderSet(string shader) => InstanceSetShader(GetContext()?.InstanceId ?? 0, shader);

    [PgslCommand("ShaderSetParameter", "ShaderSetParameter(name, value)",
        "Set one of this instance's shader parameters, by its name in GenesisParameters", "Shaders")]
    public static void ShaderSetParameter(string name, double value) =>
        InstanceSetShaderParameter(GetContext()?.InstanceId ?? 0, name, value);

    [PgslCommand("ShaderSetVector", "ShaderSetVector(name, x, y, z, w)",
        "Set a float2, float3 or float4 shader parameter of this instance (extra components are ignored)", "Shaders")]
    public static void ShaderSetVector(string name, double x, double y, double z, double w) =>
        InstanceSetShaderVector(GetContext()?.InstanceId ?? 0, name, x, y, z, w);

    [PgslCommand("InstanceSetShader", "InstanceSetShader(id, shader)", "Draw another instance with a Shader resource (empty for none)", "Shaders")]
    public static void InstanceSetShader(double id, string shader)
    {
        if (TryDrawAssets(id, out ObjectDrawAssetEntry assets)) assets.Shader = shader?.Trim() ?? string.Empty;
    }

    [PgslCommand("InstanceSetShaderParameter", "InstanceSetShaderParameter(id, name, value)",
        "Set one of another instance's shader parameters", "Shaders")]
    public static void InstanceSetShaderParameter(double id, string name, double value)
    {
        if (!string.IsNullOrWhiteSpace(name) && double.IsFinite(value) && TryDrawAssets(id, out ObjectDrawAssetEntry assets))
            ShaderValues(assets, name, 1)[0] = (float)value;
    }

    // An instance's values for one parameter, of this many components: the array it has (a game sets
    // them before every draw, and a new array each time was garbage on every draw), or a new one.
    private static float[] ShaderValues(ObjectDrawAssetEntry assets, string name, int count)
    {
        string key = name.Trim();
        if (!assets.ShaderParameters.TryGetValue(key, out float[] values) || values is null || values.Length != count)
            assets.ShaderParameters[key] = values = new float[count];
        return values;
    }

    [PgslCommand("InstanceSetShaderVector", "InstanceSetShaderVector(id, name, x, y, z, w)",
        "Set a float2, float3 or float4 shader parameter of another instance", "Shaders")]
    public static void InstanceSetShaderVector(double id, string name, double x, double y, double z, double w)
    {
        if (string.IsNullOrWhiteSpace(name) || !TryDrawAssets(id, out ObjectDrawAssetEntry assets)) return;
        float[] values = ShaderValues(assets, name, 4);
        values[0] = (float)x; values[1] = (float)y; values[2] = (float)z; values[3] = (float)w;
    }

    [PgslCommand("InstanceGetShaderParameter", "InstanceGetShaderParameter(id, name) -> number",
        "A shader parameter set on an instance (its first component); 0 when it has none of that name", "Shaders")]
    public static double InstanceGetShaderParameter(double id, string name) =>
        !string.IsNullOrWhiteSpace(name) && TryDrawAssets(id, out ObjectDrawAssetEntry assets)
            && assets.ShaderParameters.TryGetValue(name.Trim(), out float[] value) && value.Length > 0
            ? value[0]
            : 0;

    private static bool TryDrawAssets(double id, out ObjectDrawAssetEntry assets)
    {
        assets = null;
        var world = World;
        if (world is null || !double.IsFinite(id) || id < 1 || id > int.MaxValue) return false;
        Entity entity = world.GetEntity((int)id);
        if (!world.IsAlive(entity)) return false;
        if (!ObjectDrawAssetRegistry.TryGet(entity, out assets))
        {
            assets = new ObjectDrawAssetEntry();
            ObjectDrawAssetRegistry.Set(entity, assets);
        }
        return true;
    }
}
