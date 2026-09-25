using System.Numerics;
using Genesis.Shared.ECS;

namespace Genesis.Shared.ECS.Components
{
    public struct Transform3DComponent : IComponent
    {
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;

        /// <summary>Pose at the previous fixed physics step (R7.12 render interpolation).</summary>
        public Vector3 PreviousPosition;

        /// <summary>Orientation at the previous fixed physics step (R7.12 render interpolation).</summary>
        public Quaternion PreviousRotation;

        /// <summary>1 when <see cref="PreviousPosition"/>/<see cref="PreviousRotation"/> are valid.</summary>
        public byte PoseHistoryValid;

        public static Transform3DComponent Default => new()
        {
            Position = Vector3.Zero,
            Rotation = Quaternion.Identity,
            Scale = Vector3.One,
            PreviousPosition = Vector3.Zero,
            PreviousRotation = Quaternion.Identity,
            PoseHistoryValid = 0,
        };

        public Matrix4x4 WorldMatrix =>
            Matrix4x4.CreateScale(Scale)
            * Matrix4x4.CreateFromQuaternion(Rotation)
            * Matrix4x4.CreateTranslation(Position);

        /// <summary>
        /// Render-only pose between the last two fixed steps. Gameplay / queries keep using
        /// <see cref="Position"/> / <see cref="WorldMatrix"/>.
        /// </summary>
        public Matrix4x4 InterpolatedWorldMatrix(float alpha)
        {
            if (PoseHistoryValid == 0 || alpha <= 0f)
                return WorldMatrix;
            if (alpha >= 1f)
                return WorldMatrix;

            Vector3 position = Vector3.Lerp(PreviousPosition, Position, alpha);
            Quaternion rotation = Quaternion.Normalize(
                Quaternion.Slerp(PreviousRotation, Rotation, alpha));
            return Matrix4x4.CreateScale(Scale)
                * Matrix4x4.CreateFromQuaternion(rotation)
                * Matrix4x4.CreateTranslation(position);
        }
    }
}
