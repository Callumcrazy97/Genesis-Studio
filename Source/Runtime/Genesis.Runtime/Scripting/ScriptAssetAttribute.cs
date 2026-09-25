using System;
using System.Reflection;

namespace Genesis.Runtime.Scripting
{
    /// <summary>Marks a public script field as a project asset reference for inspector dropdowns.</summary>
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class ScriptAssetAttribute : Attribute
    {
        public ScriptAssetKind Kind { get; }

        public ScriptAssetAttribute(ScriptAssetKind kind) => Kind = kind;
    }

    public enum ScriptAssetKind
    {
        Any,
        Sprite,
        Texture,
        Shader,
        Script,
        Audio,
        Model,
        Particle,
        Physics,
        Object,
        Room,
    }

    /// <summary>Resolves script field asset kind from attributes and naming conventions.</summary>
    public static class ScriptAssetFieldRules
    {
        public static bool TryGetAssetKind(FieldInfo fi, out ScriptAssetKind kind)
        {
            kind = ScriptAssetKind.Any;
            if (fi == null || fi.FieldType != typeof(string))
                return false;

            var attr = fi.GetCustomAttribute<ScriptAssetAttribute>();
            if (attr != null)
            {
                kind = attr.Kind;
                return true;
            }

            string name = fi.Name;
            if (name.EndsWith("Atlas", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Sprite", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Texture", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Sprite;
                return true;
            }

            if (name.EndsWith("Shader", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Shader;
                return true;
            }

            if (name.EndsWith("Script", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Behaviour", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Script;
                return true;
            }

            if (name.EndsWith("Sound", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Audio", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Audio;
                return true;
            }

            if (name.EndsWith("Model", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Model;
                return true;
            }

            if (name.EndsWith("Particle", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Particle;
                return true;
            }

            if (name.EndsWith("Physics", StringComparison.OrdinalIgnoreCase))
            {
                kind = ScriptAssetKind.Physics;
                return true;
            }

            return false;
        }
    }
}
