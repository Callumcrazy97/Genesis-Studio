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
