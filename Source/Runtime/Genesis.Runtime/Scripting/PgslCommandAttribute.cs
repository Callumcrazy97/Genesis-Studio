using System;

namespace Genesis.Shared.Scripting
{
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Property, AllowMultiple = true)]
    public sealed class PgslCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Signature { get; }
        public string Description { get; }
        public string Category { get; }
        /// <summary>Optional additive command namespace, for example <c>Engine.Rendering</c>.</summary>
        public string Namespace { get; set; } = string.Empty;
        public bool IsImplemented { get; set; } = true;

        public PgslCommandAttribute(string name, string signature, string description, string category)
        {
            Name = name;
            Signature = signature;
            Description = description;
            Category = category;
        }
    }

    /// <summary>
    /// On a command's <c>params</c> parameter: the command neither keeps the array nor calls back into
    /// scripts while it runs, so each call may be handed the same per-thread array instead of a new one
    /// (Choose, list creation: an array per call in the 17 Oct allocation traces).
    /// </summary>
    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class ParamsNotKeptAttribute : Attribute
    {
    }

    public sealed class PgslCommandInfo
    {
        public string Name { get; set; }
        public string Signature { get; set; }
        public string CSharpMember { get; set; }
        public string Description { get; set; }
        public string Category { get; set; }
        public string Namespace { get; set; } = string.Empty;
        public string QualifiedName => string.IsNullOrWhiteSpace(Namespace) ? Name : Namespace + "." + Name;
        public bool IsProperty { get; set; }
        public bool IsImplemented { get; set; } = true;
    }
}
