namespace Genesis.Shared.ECS.Components
{
    /// <summary>
    /// Issue 7 (declarative physics): the third of the three authoring fields
    /// (<c>Physics</c> = asset name, <c>RigidBody</c> = shape, <c>PhysicsType</c> = this enum)
    /// that drive <see cref="Genesis.Physics.PhysicsDeclarativeBinding"/>. Selects how an object
    /// participates in the simulation once it has a <c>Physics</c> asset bound — see
    /// Documentation/Physics_Editor_Design.md §9 and the terrain/rendering fix plan's Issue 7.
    /// </summary>
    public enum PhysicsType
    {
        /// <summary>Default: a normal collidable body. Motion (static/dynamic) comes from the bound asset's "Default Body" settings.</summary>
        Object,
        /// <summary>Forces a dynamic body regardless of what the bound asset's default motion is.</summary>
        Dynamic,
        /// <summary>Forces a static (immovable) body regardless of what the bound asset's default motion is.</summary>
        Static,
        /// <summary>A controllable capsule character body (see <see cref="RigidBodyComponent.Character"/>).</summary>
        Character,
        /// <summary>
        /// No solid collider is created; instead the object is treated as a buoyancy volume
        /// (paired with <c>Genesis.Physics.Buoyancy</c> / <c>WaterBody</c> machinery) that floats
        /// dynamic bodies which enter it rather than blocking them.
        /// </summary>
        WaterBody,
        /// <summary>Non-solid overlap volume — same as <see cref="Sensor"/>, kept as a separate name for authoring clarity (e.g. level-trigger zones vs. gameplay sensors).</summary>
        Trigger,
        /// <summary>Non-solid overlap volume — generates contact/overlap events but no collision response (<see cref="RigidBodyFlags.Sensor"/>).</summary>
        Sensor,
    }
}
