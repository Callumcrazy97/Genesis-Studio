using System.Collections.Generic;

namespace Genesis.Shared.Scripting
{
    /// <summary>
    /// The Physics editor's declarative body fields (shape, mass, friction and so on), listed
    /// whether or not a live member backs them yet (see PgslCommandRegistry.GetPhysicsCatalog).
    /// Callable PGSL commands are never listed here: they come only from methods and properties
    /// marked [PgslCommand], so the command browser and the language cannot disagree.
    /// </summary>
    public static class PgslCommandCatalogSeed
    {
        public static readonly PgslSeedEntry[] All =
        {
            new PgslSeedEntry("Physics", "Physics = <PresetName>", "Genesis.Physics.BodyTags.Physics", "Selects a named physics preset/profile for this object, e.g. \"Physics = Default\" for the engine's built-in rigid-body preset", "Physics"),
            new PgslSeedEntry("BodyType", "BodyType = RigidBody | Kinematic | Static | Trigger", "Genesis.Physics.BodyTags.BodyType", "How the body is simulated: RigidBody (full dynamics), Kinematic (script-driven, pushes others), Static (immovable), Trigger (collision events only, no response)", "Physics"),
            new PgslSeedEntry("BodyShape", "BodyShape = Cube | Cylinder | Sphere | Capsule", "Genesis.Physics.BodyTags.BodyShape", "Collision shape used for this body's bounding volume", "Physics"),
            new PgslSeedEntry("BodyLength", "BodyLength = <units>", "Genesis.Physics.BodyTags.BodyLength", "Body extent along Z (depth), e.g. \"BodyLength = BlockSize*2\"", "Physics"),
            new PgslSeedEntry("BodyWidth", "BodyWidth = <units>", "Genesis.Physics.BodyTags.BodyWidth", "Body extent along X (width), e.g. \"BodyWidth = BlockSize*2\"", "Physics"),
            new PgslSeedEntry("BodyHeight", "BodyHeight = <units>", "Genesis.Physics.BodyTags.BodyHeight", "Body extent along Y (height), e.g. \"BodyHeight = BlockSize*2\"", "Physics"),
            new PgslSeedEntry("BodyOrigin", "BodyOrigin = CentrePoint | BottomPoint | TopPoint | LeftPoint | RightPoint | TopLeft | TopRight | BottomLeft | BottomRight | (x, y, z)", "Genesis.Physics.BodyTags.BodyOrigin", "Which point of the body's bounds maps to the object's world position — a named anchor preset, or a raw (x, y, z) offset for full control", "Physics"),
        };
    }

    /// <summary>One row of the Physics body field table.</summary>
    public readonly struct PgslSeedEntry
    {
        public readonly string Name;
        public readonly string Signature;
        public readonly string CSharpMap;
        public readonly string Description;
        public readonly string Category;

        public PgslSeedEntry(string name, string signature, string csharpMap, string description, string category)
        {
            Name = name;
            Signature = signature;
            CSharpMap = csharpMap;
            Description = description;
            Category = category;
        }
    }
}
