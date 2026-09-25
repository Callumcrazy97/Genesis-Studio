using System;
using System.IO;

namespace Genesis.Runtime
{
    /// <summary>
    /// Engine-level branding shared by every game built on Genesis: the engine name and the
    /// location of the logo asset. A consistent boot/splash experience can be built on top of
    /// this regardless of which game is running.
    /// </summary>
    public static class GenesisBranding
    {
        public const string EngineName = "Genesis Engine";

        /// <summary>The transparent mark used by the engine splash and native game window.</summary>
        public const string DefaultEmblemRelativePath = "Assets/Genesis_Studio_Emblem.png";

        /// <summary>Resolves the default Genesis splash, with an explicit project image winning.</summary>
        /// <remarks>
        /// The override is an input rather than a settings convention on purpose: the Player ships
        /// Genesis branding today, while a future project setting can supply its own resolved path
        /// without changing this runtime layer or teaching it about Studio preferences.
        /// </remarks>
        public static string ResolveSplashPath(string explicitOverride = null)
            => ResolveSplashPath(AppContext.BaseDirectory, explicitOverride);

        /// <summary>Resolves branding from a particular executable directory.</summary>
        public static string ResolveSplashPath(string baseDirectory, string explicitOverride)
        {
            if (string.IsNullOrWhiteSpace(baseDirectory))
                baseDirectory = AppContext.BaseDirectory;

            string[] candidates =
            {
                explicitOverride,
                Path.Combine(baseDirectory, "Assets", "Genesis_Studio_Emblem.png"),
                Path.Combine(baseDirectory, "Assets", "Genesis_Studio_Logo.png"),
                // Legacy installations keep their old artwork rather than losing the splash.
                Path.Combine(baseDirectory, "logo.png"),
                Path.Combine(baseDirectory, "..", "logo.png"),
                Path.Combine(baseDirectory, "..", "..", "Ember", "logo.png"),
                Path.Combine(baseDirectory, "..", "Ember", "logo.png"),
                Path.Combine(baseDirectory, "..", "..", "..", "Ember", "logo.png"),
                Path.Combine(baseDirectory, "..", "..", "logo.png"),
                Path.Combine(baseDirectory, "..", "..", "..", "logo.png"),
            };

            foreach (string candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full))
                        return full;
                }
                catch (Exception exception) when (
                    exception is ArgumentException
                    or NotSupportedException
                    or PathTooLongException)
                {
                    // A malformed optional override must not suppress the shipped fallback.
                }
            }

            return null;
        }

        /// <summary>Resolves the engine logo (logo.png) by probing the usual layout locations.</summary>
        public static string ResolveLogoPath() => ResolveSplashPath();
    }
}
