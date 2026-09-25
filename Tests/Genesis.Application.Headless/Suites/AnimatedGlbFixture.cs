using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Genesis.Application.Headless.Suites;

/// <summary>A tiny, self-contained glTF 2.0 acceptance asset: hierarchy, two materials, embedded
/// texture, custom metadata, a two-joint skin and one visibly deforming clip.</summary>
internal static class AnimatedGlbFixture
{
    private static readonly byte[] PixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+XwM7WQAAAABJRU5ErkJggg==");

    public static string Write(string directory)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Animated Intake.glb");
        List<byte> binary = [];
        JsonArray views = [];
        JsonArray accessors = [];

        int positions = AddAccessor(Floats(
            -0.5f, 0f, 0f, 0.5f, 0f, 0f, -0.5f, 1f, 0f, 0.5f, 1f, 0f), 5126, 4, "VEC3");
        int normals = AddAccessor(Floats(
            0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f, 0f, 0f, 1f), 5126, 4, "VEC3");
        int uvs = AddAccessor(Floats(0f, 1f, 1f, 1f, 0f, 0f, 1f, 0f), 5126, 4, "VEC2");
        int joints = AddAccessor([
            0, 0, 0, 0,
            0, 0, 0, 0,
            1, 0, 0, 0,
            1, 0, 0, 0,
        ], 5121, 4, "VEC4");
        int weights = AddAccessor(Floats(
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f,
            1f, 0f, 0f, 0f), 5126, 4, "VEC4");

        int indexView = AddView(UShorts(0, 1, 2, 2, 1, 3));
        int indicesA = AddAccessorForView(indexView, 0, 5123, 3, "SCALAR");
        int indicesB = AddAccessorForView(indexView, 6, 5123, 3, "SCALAR");

        Matrix4x4 inverseRoot = Matrix4x4.Identity;
        Matrix4x4 inverseArm = Matrix4x4.CreateTranslation(0f, -0.5f, 0f);
        int inverseBind = AddAccessor(Floats(MatrixValues(inverseRoot).Concat(MatrixValues(inverseArm)).ToArray()), 5126, 2, "MAT4");
        int times = AddAccessor(Floats(0f, 1f), 5126, 2, "SCALAR");
        float sine = MathF.Sin(MathF.PI / 4f);
        float cosine = MathF.Cos(MathF.PI / 4f);
        int rotations = AddAccessor(Floats(
            0f, 0f, 0f, 1f,
            0f, 0f, sine, cosine), 5126, 2, "VEC4");
        int imageView = AddView(PixelPng);

        JsonObject root = new()
        {
            ["asset"] = new JsonObject { ["version"] = "2.0", ["generator"] = "Genesis acceptance fixture" },
            ["scene"] = 0,
            ["scenes"] = new JsonArray(new JsonObject { ["nodes"] = new JsonArray(0) }),
            ["nodes"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "SettlementRoot",
                    ["mesh"] = 0,
                    ["skin"] = 0,
                    ["children"] = new JsonArray(1),
                    ["extras"] = new JsonObject { ["footprint_x"] = 2, ["total_beds"] = 4, ["role"] = "acceptance" },
                },
                new JsonObject { ["name"] = "AnimatedArm", ["translation"] = new JsonArray(0f, 0.5f, 0f) }),
            ["meshes"] = new JsonArray(new JsonObject
            {
                ["name"] = "TwoMaterialCharacter",
                ["extras"] = new JsonObject { ["collision"] = "capsule" },
                ["primitives"] = new JsonArray(
                    Primitive(indicesA, 0),
                    Primitive(indicesB, 1)),
            }),
            ["materials"] = new JsonArray(
                new JsonObject
                {
                    ["name"] = "Warm Cloth",
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["baseColorFactor"] = new JsonArray(0.9f, 0.25f, 0.12f, 1f),
                        ["baseColorTexture"] = new JsonObject { ["index"] = 0 },
                        ["metallicFactor"] = 0f,
                        ["roughnessFactor"] = 0.8f,
                    },
                },
                new JsonObject
                {
                    ["name"] = "Cool Cloth",
                    ["doubleSided"] = true,
                    ["pbrMetallicRoughness"] = new JsonObject
                    {
                        ["baseColorFactor"] = new JsonArray(0.12f, 0.35f, 0.9f, 1f),
                        ["metallicFactor"] = 0f,
                        ["roughnessFactor"] = 0.65f,
                    },
                }),
            ["textures"] = new JsonArray(new JsonObject { ["source"] = 0 }),
            ["images"] = new JsonArray(new JsonObject { ["name"] = "Pixel", ["bufferView"] = imageView, ["mimeType"] = "image/png" }),
            ["skins"] = new JsonArray(new JsonObject
            {
                ["name"] = "AcceptanceRig",
                ["joints"] = new JsonArray(0, 1),
                ["skeleton"] = 0,
                ["inverseBindMatrices"] = inverseBind,
            }),
            ["animations"] = new JsonArray(new JsonObject
            {
                ["name"] = "Wave",
                ["samplers"] = new JsonArray(new JsonObject { ["input"] = times, ["output"] = rotations, ["interpolation"] = "LINEAR" }),
                ["channels"] = new JsonArray(new JsonObject
                {
                    ["sampler"] = 0,
                    ["target"] = new JsonObject { ["node"] = 1, ["path"] = "rotation" },
                }),
            }),
            ["bufferViews"] = views,
            ["accessors"] = accessors,
            ["buffers"] = new JsonArray(new JsonObject { ["byteLength"] = binary.Count }),
        };

        byte[] json = Encoding.UTF8.GetBytes(root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        int jsonLength = json.Length;
        Array.Resize(ref json, Align4(json.Length));
        for (int i = jsonLength; i < json.Length; i++) json[i] = 0x20;
        while ((binary.Count & 3) != 0) binary.Add(0);

        byte[] output = new byte[12 + 8 + json.Length + 8 + binary.Count];
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(0), 0x46546C67);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(8), checked((uint)output.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(12), checked((uint)json.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(16), 0x4E4F534A);
        json.CopyTo(output, 20);
        int binHeader = 20 + json.Length;
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binHeader), checked((uint)binary.Count));
        BinaryPrimitives.WriteUInt32LittleEndian(output.AsSpan(binHeader + 4), 0x004E4942);
        binary.ToArray().CopyTo(output, binHeader + 8);
        File.WriteAllBytes(path, output);
        return path;

        JsonObject Primitive(int indices, int material) => new()
        {
            ["attributes"] = new JsonObject
            {
                ["POSITION"] = positions,
                ["NORMAL"] = normals,
                ["TEXCOORD_0"] = uvs,
                ["JOINTS_0"] = joints,
                ["WEIGHTS_0"] = weights,
            },
            ["indices"] = indices,
            ["material"] = material,
            ["mode"] = 4,
        };

        int AddAccessor(byte[] data, int componentType, int count, string type)
            => AddAccessorForView(AddView(data), 0, componentType, count, type);

        int AddAccessorForView(int view, int offset, int componentType, int count, string type)
        {
            int index = accessors.Count;
            accessors.Add(new JsonObject
            {
                ["bufferView"] = view,
                ["byteOffset"] = offset,
                ["componentType"] = componentType,
                ["count"] = count,
                ["type"] = type,
            });
            return index;
        }

        int AddView(byte[] data)
        {
            while ((binary.Count & 3) != 0) binary.Add(0);
            int offset = binary.Count;
            binary.AddRange(data);
            int index = views.Count;
            views.Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = offset, ["byteLength"] = data.Length });
            return index;
        }
    }

    /// <summary>Writes the same acceptance asset as JSON glTF plus an external binary buffer.</summary>
    public static string WriteGltf(string directory)
    {
        string glbPath = Write(directory);
        byte[] glb = File.ReadAllBytes(glbPath);
        int jsonLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(12, 4)));
        string json = Encoding.UTF8.GetString(glb, 20, jsonLength).TrimEnd(' ', '\0');
        int binaryHeader = 20 + jsonLength;
        int binaryLength = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(glb.AsSpan(binaryHeader, 4)));
        byte[] binary = glb.AsSpan(binaryHeader + 8, binaryLength).ToArray();

        JsonObject root = JsonNode.Parse(json)?.AsObject()
            ?? throw new InvalidDataException("The generated GLB contains no JSON object.");
        const string bufferName = "Animated Intake.bin";
        root["buffers"]![0]!["uri"] = bufferName;
        string gltfPath = Path.Combine(directory, "Animated Intake.gltf");
        File.WriteAllText(gltfPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllBytes(Path.Combine(directory, bufferName), binary);
        return gltfPath;
    }

    private static byte[] Floats(params float[] values)
    {
        byte[] bytes = new byte[values.Length * 4];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), values[i]);
        return bytes;
    }

    private static byte[] UShorts(params ushort[] values)
    {
        byte[] bytes = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), values[i]);
        return bytes;
    }

    private static float[] MatrixValues(Matrix4x4 matrix) =>
    [
        matrix.M11, matrix.M12, matrix.M13, matrix.M14,
        matrix.M21, matrix.M22, matrix.M23, matrix.M24,
        matrix.M31, matrix.M32, matrix.M33, matrix.M34,
        matrix.M41, matrix.M42, matrix.M43, matrix.M44,
    ];

    private static int Align4(int value) => (value + 3) & ~3;
}
