using System;

namespace Genesis.Runtime.Scene
{
    /// <summary>
    /// The one rule for deciding whether a spawned instance is "the Player" when a designer types
    /// that name — used by PGSL collision queries and by viewport follow targets.
    /// </summary>
    /// <remarks>
    /// Shared rather than reimplemented because two independent descriptions of the same matching
    /// rule are free to disagree, which is the failure family behind NEXT-041, NEXT-044 and
    /// NEXT-046. A viewport that follows a different Player from the one a collision query finds
    /// would be exactly that bug wearing new clothes.
    /// </remarks>
    public static class ObjectNameMatcher
    {
        /// <summary>Strips the resource suffix and normalises separators.</summary>
        public static string Normalize(string value)
        {
            string normalized = (value ?? string.Empty).Trim().Replace('\\', '/');
            const string suffix = ".object.json";
            return normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
                ? normalized[..^suffix.Length]
                : normalized;
        }

        /// <summary>
        /// True when <paramref name="actualPrefab"/> is the object <paramref name="requested"/>
        /// names. A bare leaf name matches a full project-relative path, so a designer can write
        /// "Player" for "Assets/Objects/Player.object.json".
        /// </summary>
        public static bool Matches(string actualPrefab, string requested)
        {
            if (string.IsNullOrWhiteSpace(requested)) return true;
            if (string.IsNullOrWhiteSpace(actualPrefab)) return false;

            string actual = Normalize(actualPrefab);
            string wanted = Normalize(requested);
            if (actual.Equals(wanted, StringComparison.OrdinalIgnoreCase)) return true;

            return Leaf(actual).Equals(Leaf(wanted), StringComparison.OrdinalIgnoreCase);
        }

        private static string Leaf(string value)
        {
            int slash = value.LastIndexOf('/');
            return slash >= 0 ? value[(slash + 1)..] : value;
        }
    }
}
