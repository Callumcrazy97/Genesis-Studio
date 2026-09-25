using System;
using System.Numerics;
using Genesis.Shared.Interfaces;

namespace Genesis.World.Water
{
    /// <summary>Packed shallow-water state suitable for rendering/debug upload.</summary>
    public readonly struct WaterSimulationCell
    {
        public readonly float Height;
        public readonly Vector2 Velocity;
        public readonly float Foam;
        public readonly float Depth;
        public readonly bool Wet;

        public WaterSimulationCell(float height, Vector2 velocity, float foam, float depth, bool wet)
        {
            Height = height;
            Velocity = velocity;
            Foam = foam;
            Depth = depth;
            Wet = wet;
        }
    }

    /// <summary>
    /// Conservative height-field shallow-water solver adapted from Aetherforge's fluid system.
    /// Height is a zero-mean displacement around the authored WaterBody surface; dry cells reflect
    /// waves, and a CFL-derived substep count keeps small/deep bodies stable.
    /// </summary>
    public sealed class ShallowWaterGrid
    {
        private readonly int _width;
        private readonly int _depthCount;
        private float[] _height;
        private float[] _heightNext;
        private float[] _velocityX;
        private float[] _velocityZ;
        private float[] _velocityXNext;
        private float[] _velocityZNext;
        private float[] _foam;
        private float[] _foamNext;
        private readonly float[] _depth;
        private readonly bool[] _wet;
        private readonly WaterSimulationCell[] _cells;

        public ShallowWaterGrid(int width, int depth)
        {
            if (width < 8 || depth < 8) throw new ArgumentOutOfRangeException(nameof(width));
            _width = width;
            _depthCount = depth;
            int count = width * depth;
            _height = new float[count];
            _heightNext = new float[count];
            _velocityX = new float[count];
            _velocityZ = new float[count];
            _velocityXNext = new float[count];
            _velocityZNext = new float[count];
            _foam = new float[count];
            _foamNext = new float[count];
            _depth = new float[count];
            _wet = new bool[count];
            _cells = new WaterSimulationCell[count];
        }

        public int Width => _width;
        public int Depth => _depthCount;
        public ReadOnlyMemory<WaterSimulationCell> Cells => _cells;

        public void Configure(Func<float, float, (bool Wet, float Depth)> sample)
        {
            if (sample == null) throw new ArgumentNullException(nameof(sample));
            for (int z = 0; z < _depthCount; z++)
            {
                float v = z / (float)(_depthCount - 1);
                for (int x = 0; x < _width; x++)
                {
                    float u = x / (float)(_width - 1);
                    int index = Index(x, z);
                    (bool wet, float depth) = sample(u, v);
                    _wet[index] = wet;
                    _depth[index] = MathF.Max(0.001f, depth);
                }
            }

            Reset();
            PackCells();
        }

        public void Reset()
        {
            Array.Clear(_height);
            Array.Clear(_heightNext);
            Array.Clear(_velocityX);
            Array.Clear(_velocityZ);
            Array.Clear(_velocityXNext);
            Array.Clear(_velocityZNext);
            Array.Clear(_foam);
            Array.Clear(_foamNext);
        }

        public void AddImpulse(float u, float v, float radius, float heightImpulse,
            Vector2 velocityImpulse, float foam)
        {
            float centerX = u * (_width - 1);
            float centerZ = v * (_depthCount - 1);
            float radiusX = MathF.Max(1f, radius * _width);
            float radiusZ = MathF.Max(1f, radius * _depthCount);
            int minX = Math.Max(0, (int)MathF.Floor(centerX - radiusX));
            int maxX = Math.Min(_width - 1, (int)MathF.Ceiling(centerX + radiusX));
            int minZ = Math.Max(0, (int)MathF.Floor(centerZ - radiusZ));
            int maxZ = Math.Min(_depthCount - 1, (int)MathF.Ceiling(centerZ + radiusZ));
            for (int z = minZ; z <= maxZ; z++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float dx = (x - centerX) / radiusX;
                    float dz = (z - centerZ) / radiusZ;
                    float distanceSquared = dx * dx + dz * dz;
                    int index = Index(x, z);
                    if (distanceSquared >= 1f || !_wet[index]) continue;
                    float weight = 1f - distanceSquared;
                    weight *= weight;
                    _height[index] += heightImpulse * weight;
                    _velocityX[index] += velocityImpulse.X * weight;
                    _velocityZ[index] += velocityImpulse.Y * weight;
                    _foam[index] = MathF.Max(_foam[index], foam * weight);
                }
            }
        }

        public void Step(float deltaSeconds, float cellSizeX, float cellSizeZ,
            float gravity = 9.81f, float damping = 0.992f, float foamDecay = 0.55f)
        {
            float dt = Math.Clamp(deltaSeconds, 0.0005f, 1f / 30f);
            float dx = MathF.Max(cellSizeX, 0.0005f);
            float dz = MathF.Max(cellSizeZ, 0.0005f);
            float maximumDepth = 0.001f;
            for (int i = 0; i < _depth.Length; i++)
                if (_wet[i]) maximumDepth = MathF.Max(maximumDepth, _depth[i] + MathF.Max(_height[i], 0f));

            float waveSpeed = MathF.Sqrt(MathF.Max(0.01f, gravity * maximumDepth));
            float cfl = dt * waveSpeed / MathF.Min(dx, dz);
            int substeps = Math.Clamp((int)MathF.Ceiling(cfl / 0.62f), 1, 18);
            float substep = dt / substeps;
            for (int i = 0; i < substeps; i++)
                StepOnce(substep, dx, dz, gravity, damping, foamDecay);

            RemoveMeanHeight();
            PackCells();
        }

        private void StepOnce(float dt, float dx, float dz, float gravity, float damping, float foamDecay)
        {
            for (int z = 0; z < _depthCount; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int index = Index(x, z);
                    if (!_wet[index])
                    {
                        _velocityXNext[index] = _velocityZNext[index] = 0f;
                        continue;
                    }

                    float center = _height[index];
                    float left = HeightOrCenter(x - 1, z, center);
                    float right = HeightOrCenter(x + 1, z, center);
                    float back = HeightOrCenter(x, z - 1, center);
                    float front = HeightOrCenter(x, z + 1, center);
                    float accelerationX = -gravity * (right - left) / (2f * dx);
                    float accelerationZ = -gravity * (front - back) / (2f * dz);
                    float speed = MathF.Sqrt(_velocityX[index] * _velocityX[index]
                        + _velocityZ[index] * _velocityZ[index]);
                    float drag = MathF.Pow(Math.Clamp(damping, 0.8f, 1f), dt * 60f)
                        / (1f + speed * dt * 0.018f);
                    float nextX = (_velocityX[index] + accelerationX * dt) * drag;
                    float nextZ = (_velocityZ[index] + accelerationZ * dt) * drag;
                    if (!IsWet(x - 1, z) && nextX < 0f) nextX = 0f;
                    if (!IsWet(x + 1, z) && nextX > 0f) nextX = 0f;
                    if (!IsWet(x, z - 1) && nextZ < 0f) nextZ = 0f;
                    if (!IsWet(x, z + 1) && nextZ > 0f) nextZ = 0f;
                    _velocityXNext[index] = nextX;
                    _velocityZNext[index] = nextZ;
                }
            }

            for (int z = 0; z < _depthCount; z++)
            {
                for (int x = 0; x < _width; x++)
                {
                    int index = Index(x, z);
                    if (!_wet[index])
                    {
                        _heightNext[index] = _foamNext[index] = 0f;
                        continue;
                    }

                    float center = _height[index];
                    float depth = MathF.Max(0.001f, _depth[index] + center);
                    float divergence = (FluxX(x, z, x + 1, depth) - FluxX(x, z, x - 1, depth)) / dx
                        + (FluxZ(x, z, z + 1, depth) - FluxZ(x, z, z - 1, depth)) / dz;
                    float left = HeightOrCenter(x - 1, z, center);
                    float right = HeightOrCenter(x + 1, z, center);
                    float back = HeightOrCenter(x, z - 1, center);
                    float front = HeightOrCenter(x, z + 1, center);
                    float laplacian = left + right + back + front - 4f * center;
                    float height = center - divergence * dt + laplacian * MathF.Min(0.045f, dt * 1.8f);
                    float maximum = MathF.Max(0.035f, _depth[index] * 0.22f);
                    _heightNext[index] = Math.Clamp(height, -maximum, maximum);

                    float current = MathF.Sqrt(_velocityXNext[index] * _velocityXNext[index]
                        + _velocityZNext[index] * _velocityZNext[index]);
                    float curvature = MathF.Abs(laplacian) / MathF.Max(0.001f, dx + dz);
                    float generatedFoam = Math.Clamp(current * 0.045f + curvature * 0.12f, 0f, 1f);
                    _foamNext[index] = MathF.Max(
                        _foam[index] * MathF.Exp(-foamDecay * dt), generatedFoam * 0.3f);
                }
            }

            Swap(ref _height, ref _heightNext);
            Swap(ref _velocityX, ref _velocityXNext);
            Swap(ref _velocityZ, ref _velocityZNext);
            Swap(ref _foam, ref _foamNext);
        }

        private float FluxX(int x, int z, int neighbourX, float centerDepth)
        {
            if (!IsWet(neighbourX, z)) return 0f;
            int current = Index(x, z);
            int neighbour = Index(neighbourX, z);
            float neighbourDepth = MathF.Max(0.001f, _depth[neighbour] + _height[neighbour]);
            return 0.5f * (centerDepth + neighbourDepth)
                * 0.5f * (_velocityXNext[current] + _velocityXNext[neighbour]);
        }

        private float FluxZ(int x, int z, int neighbourZ, float centerDepth)
        {
            if (!IsWet(x, neighbourZ)) return 0f;
            int current = Index(x, z);
            int neighbour = Index(x, neighbourZ);
            float neighbourDepth = MathF.Max(0.001f, _depth[neighbour] + _height[neighbour]);
            return 0.5f * (centerDepth + neighbourDepth)
                * 0.5f * (_velocityZNext[current] + _velocityZNext[neighbour]);
        }

        private void RemoveMeanHeight()
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < _height.Length; i++)
            {
                if (!_wet[i]) continue;
                sum += _height[i];
                count++;
            }
            if (count == 0) return;
            float mean = (float)(sum / count);
            for (int i = 0; i < _height.Length; i++) if (_wet[i]) _height[i] -= mean;
        }

        private void PackCells()
        {
            for (int i = 0; i < _cells.Length; i++)
                _cells[i] = new WaterSimulationCell(
                    _wet[i] ? _height[i] : 0f,
                    new Vector2(_velocityX[i], _velocityZ[i]),
                    _foam[i], _depth[i], _wet[i]);
        }

        private int Index(int x, int z) => x + z * _width;
        private bool IsWet(int x, int z) => x >= 0 && z >= 0 && x < _width && z < _depthCount && _wet[Index(x, z)];
        private float HeightOrCenter(int x, int z, float center) => IsWet(x, z) ? _height[Index(x, z)] : center;
        private static void Swap(ref float[] first, ref float[] second) => (first, second) = (second, first);
    }

    /// <summary>Fixed-step world-space wrapper attaching shallow-water state to a Genesis WaterBody.</summary>
    public sealed class WaterBodySimulation
    {
        public const float FixedStepSeconds = 1f / 60f;
        private readonly ShallowWaterGrid _grid;
        private readonly Vector2 _minimum;
        private readonly Vector2 _size;
        private float _accumulator;

        public WaterBodySimulation(WaterBody body, Func<float, float, float> depthAt = null)
        {
            Body = body ?? throw new ArgumentNullException(nameof(body));
            int resolution = Math.Clamp(body.SimulationResolution, 8, 128);
            _size = new Vector2(MathF.Max(0.1f, body.SizeX), MathF.Max(0.1f, body.SizeZ));
            _minimum = new Vector2(body.Center.X, body.Center.Z) - _size * 0.5f;
            _grid = new ShallowWaterGrid(resolution, resolution);
            _grid.Configure((u, v) =>
            {
                Vector2 world = _minimum + new Vector2(u * _size.X, v * _size.Y);
                float depth = depthAt == null ? body.SimulationDepth : depthAt(world.X, world.Y);
                return (depth > 0.02f, MathF.Max(0.001f, depth));
            });
            CellSize = _size / MathF.Max(1, resolution - 1);
        }

        public WaterBody Body { get; }
        public Vector2 CellSize { get; }
        public int Resolution => _grid.Width;
        public int Revision { get; private set; }
        public ReadOnlyMemory<WaterSimulationCell> Cells => _grid.Cells;

        public int Update(float deltaSeconds)
        {
            _accumulator += Math.Clamp(deltaSeconds, 0f, FixedStepSeconds * 6f);
            int steps = 0;
            while (_accumulator >= FixedStepSeconds && steps < 6)
            {
                _grid.Step(FixedStepSeconds, CellSize.X, CellSize.Y,
                    damping: Math.Clamp(Body.SimulationDamping, 0.8f, 1f));
                _accumulator -= FixedStepSeconds;
                steps++;
            }
            if (steps > 0) Revision++;
            return steps;
        }

        public void Disturb(Vector3 worldPosition, float radiusMetres, float strength,
            Vector2 push = default, float foam = 0f)
        {
            Vector2 uv = ToGrid(new Vector2(worldPosition.X, worldPosition.Z));
            if (uv.X < -0.2f || uv.X > 1.2f || uv.Y < -0.2f || uv.Y > 1.2f) return;
            float radius = radiusMetres / MathF.Max(_size.X, _size.Y);
            _grid.AddImpulse(uv.X, uv.Y, radius, strength, push, foam);
            Revision++;
        }

        public (float Height, float Foam) SampleSurface(float worldX, float worldZ)
        {
            Vector2 uv = ToGrid(new Vector2(worldX, worldZ));
            if (uv.X < 0f || uv.X > 1f || uv.Y < 0f || uv.Y > 1f) return (0f, 0f);
            ReadOnlySpan<WaterSimulationCell> cells = _grid.Cells.Span;
            int maximum = _grid.Width - 1;
            float fx = uv.X * maximum;
            float fz = uv.Y * maximum;
            int x0 = Math.Clamp((int)fx, 0, maximum);
            int z0 = Math.Clamp((int)fz, 0, maximum);
            int x1 = Math.Min(x0 + 1, maximum);
            int z1 = Math.Min(z0 + 1, maximum);
            float tx = fx - x0;
            float tz = fz - z0;
            WaterSimulationCell c00 = cells[z0 * _grid.Width + x0];
            WaterSimulationCell c10 = cells[z0 * _grid.Width + x1];
            WaterSimulationCell c01 = cells[z1 * _grid.Width + x0];
            WaterSimulationCell c11 = cells[z1 * _grid.Width + x1];
            float h00 = c00.Wet ? c00.Height : 0f;
            float h10 = c10.Wet ? c10.Height : 0f;
            float h01 = c01.Wet ? c01.Height : 0f;
            float h11 = c11.Wet ? c11.Height : 0f;
            float f00 = c00.Wet ? c00.Foam : 0f;
            float f10 = c10.Wet ? c10.Foam : 0f;
            float f01 = c01.Wet ? c01.Foam : 0f;
            float f11 = c11.Wet ? c11.Foam : 0f;
            return (
                Lerp(Lerp(h00, h10, tx), Lerp(h01, h11, tx), tz),
                Lerp(Lerp(f00, f10, tx), Lerp(f01, f11, tx), tz));
        }

        /// <summary>Builds a displaced render mesh, including normals derived from the solver grid.</summary>
        public MeshData BuildMesh()
        {
            int resolution = _grid.Width;
            WaterSimulationCell[] cells = _grid.Cells.ToArray();
            MeshVertex[] vertices = new MeshVertex[resolution * resolution];
            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int index = z * resolution + x;
                    float left = cells[z * resolution + Math.Max(0, x - 1)].Height;
                    float right = cells[z * resolution + Math.Min(resolution - 1, x + 1)].Height;
                    float back = cells[Math.Max(0, z - 1) * resolution + x].Height;
                    float front = cells[Math.Min(resolution - 1, z + 1) * resolution + x].Height;
                    Vector3 normal = Vector3.Normalize(new Vector3(
                        -(right - left) / MathF.Max(0.001f, CellSize.X * 2f),
                        1f,
                        -(front - back) / MathF.Max(0.001f, CellSize.Y * 2f)));
                    vertices[index] = new MeshVertex
                    {
                        Position = new Vector3(
                            _minimum.X + x / (float)(resolution - 1) * _size.X,
                            Body.SurfaceY + cells[index].Height,
                            _minimum.Y + z / (float)(resolution - 1) * _size.Y),
                        Normal = normal,
                        Color = new Vector4(1f, 1f, 1f, cells[index].Wet ? 1f : 0f),
                        UV = new Vector2(x / (float)(resolution - 1), z / (float)(resolution - 1)),
                    };
                }
            }

            ushort[] indices = new ushort[(resolution - 1) * (resolution - 1) * 6];
            int write = 0;
            for (int z = 0; z < resolution - 1; z++)
            {
                for (int x = 0; x < resolution - 1; x++)
                {
                    int a = z * resolution + x;
                    int b = a + 1;
                    int c = a + resolution;
                    int d = c + 1;
                    indices[write++] = (ushort)a; indices[write++] = (ushort)d; indices[write++] = (ushort)b;
                    indices[write++] = (ushort)a; indices[write++] = (ushort)c; indices[write++] = (ushort)d;
                }
            }
            return new MeshData { Vertices = vertices, Indices = indices };
        }

        private Vector2 ToGrid(Vector2 world) => (world - _minimum) / _size;
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    }
}
