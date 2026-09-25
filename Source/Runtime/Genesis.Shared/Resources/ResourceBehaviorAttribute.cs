using System;

namespace Genesis.Shared.Assets;

/// <summary>Private compiler-emitted link from a public Script resource name to its CLR implementation.
/// Stored in the compiled game assembly so exports do not depend on source files being present.</summary>
[AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
public sealed class ResourceBehaviorAttribute : Attribute
{
    public ResourceBehaviorAttribute(string resourceName, string typeName)
    {
        ResourceName = resourceName;
        TypeName = typeName;
    }

    public string ResourceName { get; }
    public string TypeName { get; }
}
