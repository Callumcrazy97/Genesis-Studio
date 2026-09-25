using System;
using System.Collections.Generic;
using System.Globalization;

namespace Genesis.Runtime.Debugger
{
    /// <summary>
    /// Registry for in-game debug console commands (~ / tilde).
    /// Allows developers and playtesters to execute cheats, toggles, room switches, and inspect variables.
    /// </summary>
    public sealed class DebugConsoleCommandRegistry
    {
        public delegate string CommandHandler(string[] args);

        private readonly Dictionary<string, (string Description, CommandHandler Handler)> _commands =
            new(StringComparer.OrdinalIgnoreCase);

        public DebugConsoleCommandRegistry()
        {
            RegisterBuiltins();
        }

        public void Register(string name, string description, CommandHandler handler)
        {
            if (string.IsNullOrWhiteSpace(name) || handler == null) return;
            _commands[name.Trim()] = (description ?? string.Empty, handler);
        }

        public string Execute(string input)
        {
            if (string.IsNullOrWhiteSpace(input)) return string.Empty;

            string[] tokens = input.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0) return string.Empty;

            string command = tokens[0];
            string[] args = tokens.Length > 1 ? tokens[1..] : Array.Empty<string>();

            if (_commands.TryGetValue(command, out var entry))
            {
                try
                {
                    return entry.Handler(args);
                }
                catch (Exception ex)
                {
                    return $"Error executing '{command}': {ex.Message}";
                }
            }

            return $"Unknown command: '{command}'. Type 'help' for available commands.";
        }

        public IEnumerable<(string Name, string Description)> GetCommands()
        {
            foreach (var kvp in _commands)
            {
                yield return (kvp.Key, kvp.Value.Description);
            }
        }

        private void RegisterBuiltins()
        {
            Register("help", "List all available in-game console commands", _ =>
            {
                var list = new List<string>();
                foreach (var (name, desc) in GetCommands())
                {
                    list.Add($"  {name,-12} - {desc}");
                }
                return "Commands:\n" + string.Join("\n", list);
            });

            Register("clear", "Clear console history", _ => "[CLEAR]");
        }
    }
}
