using System;
using System.Numerics;
using Genesis.Runtime;
using Genesis.Runtime.Core;
using Genesis.Runtime.Input;
using Genesis.Runtime.Scene;
using Genesis.Shared.Interfaces;

namespace Genesis.World
{
    /// <summary>
    /// Fly camera plus block breaking/placing for voxel sandbox games.
    /// </summary>
    public sealed class VoxelPlayerController : ISceneSubsystem
    {
        private const float PlayerClearanceHalfXz = 0.85f;
        private const float PlayerClearanceHalfY = 1.65f;

        private static readonly VoxelBlock[] Hotbar =
        {
            VoxelBlock.Grass,
            VoxelBlock.Dirt,
            VoxelBlock.Stone,
            VoxelBlock.Sand,
            VoxelBlock.Wood,
            VoxelBlock.Cobblestone,
            VoxelBlock.Leaves,
            VoxelBlock.Gravel,
        };

        private readonly FlyCameraController _fly;
        private int _hotbarIndex;

        public VoxelWorld World { get; set; }
        public VoxelWorldRenderer Renderer { get; set; }
        public float Reach { get; set; } = 8f;
        public float MoveSpeed { get; set; } = 14f;

        public VoxelBlock SelectedBlock => Hotbar[_hotbarIndex];
        public VoxelRaycastHit LastHit { get; private set; }

        public VoxelPlayerController(Camera3D camera)
        {
            _fly = new FlyCameraController(camera) { MoveSpeed = 14f };
        }

        public void Update(RuntimeScene scene, GameTime time)
        {
            InputState input = scene.Input;
            if (input == null) return;

            _fly.MoveSpeed = MoveSpeed;
            _fly.Update(input, time.Delta, allowMovement: true);

            for (int i = 0; i < Hotbar.Length && i < 8; i++)
            {
                if (input.WasPressed(Key.D1 + i))
                    _hotbarIndex = i;
            }

            if (input.WasPressed(Key.Q))
                _hotbarIndex = (_hotbarIndex + Hotbar.Length - 1) % Hotbar.Length;
            if (input.WasPressed(Key.E))
                _hotbarIndex = (_hotbarIndex + 1) % Hotbar.Length;

            Camera3D camera = scene.Camera3D;
            Vector3 origin = camera.Position;
            Vector3 dir = camera.Forward;
            LastHit = VoxelRaycast.Cast(World, origin, dir, Reach);

            if (!LastHit.Hit) return;

            if (input.WasPressed(MouseButton.Left))
                TryBreak();

            if (input.WasPressed(MouseButton.Right))
                TryPlace(scene);
        }

        public void FixedUpdate(RuntimeScene scene, float fixedDelta) { }

        public void SubmitMeshes(RuntimeScene scene, MeshDrawCall[] buffer, ref int count, Genesis.Shared.Interfaces.IRenderController renderer) { }

        public void Dispose() { }

        private void TryBreak()
        {
            if (World == null) return;
            if (LastHit.Block == VoxelBlock.Bedrock) return;
            if (World.TrySetBlock(LastHit.BlockX, LastHit.BlockY, LastHit.BlockZ, VoxelBlock.Air, out _))
                Renderer?.NotifyBlockEdited();
        }

        private void TryPlace(RuntimeScene scene)
        {
            if (World == null) return;
            if (SelectedBlock == VoxelBlock.Air) return;

            int px = LastHit.PlaceX;
            int py = LastHit.PlaceY;
            int pz = LastHit.PlaceZ;

            Vector3 cam = scene.Camera3D.Position;
            if (MathF.Abs(px + 0.5f - cam.X) < PlayerClearanceHalfXz &&
                MathF.Abs(py + 0.5f - cam.Y) < PlayerClearanceHalfY &&
                MathF.Abs(pz + 0.5f - cam.Z) < PlayerClearanceHalfXz)
                return;

            VoxelBlock existing = World.GetBlock(px, py, pz);
            if (existing != VoxelBlock.Air && VoxelBlocks.IsSolid(existing))
                return;

            if (World.TrySetBlock(px, py, pz, SelectedBlock, out _))
                Renderer?.NotifyBlockEdited();
        }
    }
}
