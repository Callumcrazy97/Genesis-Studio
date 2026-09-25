using System;
using System.Collections.Generic;
using System.Reflection;

namespace Genesis.Shared.Scripting
{
    public static class PgslCommandRegistry
    {
        private static List<PgslCommandInfo> _catalog;
        private static Dictionary<string, PgslCommandInfo> _byName;
        private static Dictionary<string, PgslCommandInfo> _byQualifiedName;

        public static void Build(Type commandHostType)
        {
            _catalog = new List<PgslCommandInfo>(256);
            _byName = new Dictionary<string, PgslCommandInfo>(StringComparer.OrdinalIgnoreCase);
            _byQualifiedName = new Dictionary<string, PgslCommandInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var method in commandHostType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (var attribute in method.GetCustomAttributes<PgslCommandAttribute>())
                    Register(attribute, method.Name, false);
            }

            foreach (var property in commandHostType.GetProperties(BindingFlags.Public | BindingFlags.Static))
            {
                foreach (var attribute in property.GetCustomAttributes<PgslCommandAttribute>())
                    Register(attribute, property.Name, true);
            }

            InvalidateFullCatalog();
        }

        public static void Build()
        {
            // The runtime startup path registers Genesis.Runtime.Scripting.PgslCommands.
            if (_catalog != null) return;
        }

        public static IReadOnlyList<PgslCommandInfo> GetCatalog()
        {
            return _catalog ?? (IReadOnlyList<PgslCommandInfo>)Array.Empty<PgslCommandInfo>();
        }

        /// <summary>
        /// Resolve either a legacy flat command name or an explicitly qualified namespace command.
        /// </summary>
        public static PgslCommandInfo TryGet(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return null;
            if (_byQualifiedName != null && _byQualifiedName.TryGetValue(name, out var qualified))
                return qualified;
            if (_byName == null) return null;
            return _byName.TryGetValue(name, out var info) ? info : null;
        }

        /// <summary>
        /// Resolve a command inside a namespace, falling back to the global command surface. This is
        /// what keeps <c>from Engine.X:</c> additive rather than hiding ordinary PGSL helpers.
        /// </summary>
        public static PgslCommandInfo TryGet(string commandNamespace, string name)
        {
            if (!string.IsNullOrWhiteSpace(commandNamespace) && !string.IsNullOrWhiteSpace(name))
            {
                string qualified = commandNamespace.Trim().Trim('.') + "." + name.Trim();
                if (_byQualifiedName != null && _byQualifiedName.TryGetValue(qualified, out var namespaced))
                    return namespaced;
            }
            return TryGet(name);
        }

        private static List<PgslCommandInfo> _fullCatalog;

        /// <summary>
        /// Engine.* rows belong to EngineCommandRegistry and its editor tab, so they are not
        /// duplicated in the callable PGSL command list.
        /// </summary>
        private static bool IsEngineCommand(string name, string category)
        {
            return (name != null && name.StartsWith("Engine.", StringComparison.Ordinal))
                || string.Equals(category, "Engine Commands", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Identifies the declarative Physics body fields shown in the Physics editor tab.
        /// Callable physics functions are live attributes and remain in the PGSL catalogue.
        /// </summary>
        private static bool IsPhysicsCommand(string category) =>
            string.Equals(category, "Physics", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns only commands backed by live executable members. The legacy seed catalogue is
        /// migration/reference data, not a callable contract or an editor insertion source.
        /// </summary>
        public static IReadOnlyList<PgslCommandInfo> GetFullCatalog()
        {
            if (_fullCatalog != null) return _fullCatalog;

            var callable = new List<PgslCommandInfo>();
            if (_catalog != null)
            {
                foreach (PgslCommandInfo command in _catalog)
                {
                    if (!IsEngineCommand(command.Name, command.Category))
                        callable.Add(command);
                }
            }

            callable.Sort((left, right) =>
            {
                int category = string.Compare(left.Category, right.Category, StringComparison.Ordinal);
                if (category != 0) return category;
                int commandNamespace = string.Compare(left.Namespace, right.Namespace, StringComparison.Ordinal);
                return commandNamespace != 0
                    ? commandNamespace
                    : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            });
            _fullCatalog = callable;
            return _fullCatalog;
        }

        private static List<PgslCommandInfo> _physicsCatalog;

        /// <summary>
        /// Returns declarative Physics body fields for the Physics editor tab. They are separate
        /// from callable PGSL functions and are marked implemented only when a matching live member
        /// exists.
        /// </summary>
        public static IReadOnlyList<PgslCommandInfo> GetPhysicsCatalog()
        {
            if (_physicsCatalog != null) return _physicsCatalog;

            var result = new List<PgslCommandInfo>();
            foreach (var seed in PgslCommandCatalogSeed.All)
            {
                if (!IsPhysicsCommand(seed.Category)) continue;

                var live = (_byName != null && _byName.TryGetValue(seed.Name, out var liveInfo))
                    ? liveInfo
                    : null;
                result.Add(new PgslCommandInfo
                {
                    Name = seed.Name,
                    Signature = live?.Signature ?? seed.Signature,
                    CSharpMember = live?.CSharpMember ?? seed.CSharpMap,
                    Description = live?.Description ?? seed.Description,
                    Category = live?.Category ?? seed.Category,
                    Namespace = live?.Namespace ?? string.Empty,
                    IsProperty = live?.IsProperty ?? false,
                    IsImplemented = live != null,
                });
            }

            _physicsCatalog = result;
            return _physicsCatalog;
        }

        /// <summary>Clears cached catalogues after rebuilding the live registry.</summary>
        public static void InvalidateFullCatalog()
        {
            _fullCatalog = null;
            _physicsCatalog = null;
        }

        private static void Register(PgslCommandAttribute attribute, string csharpName, bool isProperty)
        {
            string commandNamespace = attribute.Namespace?.Trim().Trim('.') ?? string.Empty;
            string qualifiedName = commandNamespace.Length == 0
                ? attribute.Name
                : commandNamespace + "." + attribute.Name;

            if (_byQualifiedName.ContainsKey(qualifiedName)) return;

            var info = new PgslCommandInfo
            {
                Name = attribute.Name,
                Signature = attribute.Signature,
                CSharpMember = "PgslCommands." + csharpName,
                Description = attribute.Description,
                Category = attribute.Category,
                Namespace = commandNamespace,
                IsProperty = isProperty,
                IsImplemented = attribute.IsImplemented,
            };
            _catalog.Add(info);
            _byQualifiedName[qualifiedName] = info;

            // Namespace support is additive. A namespaced declaration owns its qualified name and
            // retains the legacy flat alias when that alias is not already claimed by another command.
            // This lets existing PGSL keep working while new scripts opt into Engine.X qualification.
            if (!_byName.ContainsKey(attribute.Name))
                _byName[attribute.Name] = info;
        }
    }
}
