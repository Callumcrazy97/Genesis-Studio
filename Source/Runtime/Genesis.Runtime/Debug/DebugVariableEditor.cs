using System;
using System.Collections.Generic;
using System.Globalization;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// Live variable inspector and runtime modifier for Genesis Global and Object variables.
    /// </summary>
    public sealed class DebugVariableEditor
    {
        private readonly Dictionary<string, object> _globals = new(StringComparer.OrdinalIgnoreCase);

        public DebugVariableEditor()
        {
            // Register standard default globals
            _globals["global.score"] = 0;
            _globals["global.difficulty"] = 1.0;
            _globals["global.soundVolume"] = 0.8;
            _globals["global.musicVolume"] = 0.7;
            _globals["global.gamePaused"] = false;
        }

        public IReadOnlyDictionary<string, object> Globals => _globals;

        public void SetGlobal(string name, object value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            string key = name.StartsWith("global.", StringComparison.OrdinalIgnoreCase) ? name : "global." + name;
            _globals[key] = value;
        }

        public bool TryGetGlobal(string name, out object value)
        {
            string key = name.StartsWith("global.", StringComparison.OrdinalIgnoreCase) ? name : "global." + name;
            return _globals.TryGetValue(key, out value);
        }

        public bool TrySetFromString(string name, string rawValue)
        {
            if (string.IsNullOrWhiteSpace(name)) return false;

            object parsedValue = rawValue;
            if (bool.TryParse(rawValue, out bool bVal))
                parsedValue = bVal;
            else if (int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int iVal))
                parsedValue = iVal;
            else if (double.TryParse(rawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out double dVal))
                parsedValue = dVal;

            SetGlobal(name, parsedValue);
            return true;
        }
    }
}
