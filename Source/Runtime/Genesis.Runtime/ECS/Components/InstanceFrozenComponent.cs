using Genesis.Shared.ECS;

namespace Genesis.Runtime.ECS.Components
{
    /// <summary>
    /// Marks an instance whose Step events do not run (Engine.Instance.Enabled = false): frozen
    /// where it is, still drawn and still solid, until another instance turns it back on.
    /// </summary>
    public struct InstanceFrozenComponent : IComponent
    {
        /// <summary>True while frozen; turned back on, an instance keeps the component with this false.</summary>
        public bool Frozen;

        /// <summary>
        /// Whether any instance has been frozen since the game began, so the script host checks
        /// each instance only once one has.
        /// </summary>
        public static bool Any { get; set; }
    }
}
