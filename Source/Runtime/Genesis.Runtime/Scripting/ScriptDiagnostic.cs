using System;

namespace Genesis.Runtime.Scripting
{
    /// <summary>A structured, retained runtime script failure.</summary>
    public sealed class ScriptDiagnostic
    {
        public DateTime TimestampUtc { get; init; }
        public DateTime LastOccurrenceUtc { get; internal set; }
        public string ObjectName { get; init; }
        public string BehaviorName { get; init; }
        public int EntityId { get; init; }
        public string EventName { get; init; }
        public string Hook { get; init; }
        public string Source { get; init; }
        public int Line { get; init; }
        public string Message { get; init; }
        public string ExceptionType { get; init; }
        public string StackTrace { get; init; }
        public int RepeatCount { get; internal set; } = 1;
        internal string Fingerprint { get; init; }

        public string ToDisplayString()
        {
            string owner = string.IsNullOrWhiteSpace(ObjectName) ? BehaviorName : ObjectName;
            string eventName = string.IsNullOrWhiteSpace(EventName) ? Hook : EventName;
            string location = Line > 0 ? $" line {Line}" : string.Empty;
            return $"{owner}.{eventName}{location}: {Message}";
        }

        public string ToLogLine() =>
            $"[SCRIPT ERROR] object=\"{Escape(ObjectName)}\" behavior=\"{Escape(BehaviorName)}\" "
            + $"entity={EntityId} event=\"{Escape(EventName)}\" hook=\"{Escape(Hook)}\" "
            + $"source=\"{Escape(Source)}\" line={Line} repeat={RepeatCount} "
            + $"exception=\"{Escape(ExceptionType)}\" message=\"{Escape(Message)}\"";

        private static string Escape(string value) => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    /// <summary>
    /// Carries PGSL object/event/source information through the generic behaviour host catch boundary.
    /// </summary>
    public sealed class PgslExecutionException : Exception
    {
        public string ScriptName { get; }
        public string EventName { get; }
        public string SourceLabel { get; }
        public int SourceLine { get; }
        public string ScriptMessage { get; }

        public PgslExecutionException(string scriptName, string eventName, Exception innerException)
            : base(BuildMessage(scriptName, eventName, innerException, out int line, out string scriptMessage), innerException)
        {
            ScriptName = scriptName ?? "PGSL";
            EventName = eventName ?? "Unknown";
            SourceLabel = $"{ScriptName} · {EventName}";
            SourceLine = line;
            ScriptMessage = scriptMessage;
            if (line > 0) Data["Line"] = line;
        }

        public static int FindLine(Exception exception)
        {
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                try
                {
                    if (current.Data?["Line"] is int line && line > 0) return line;
                    if (current.Data?["Line"] is long longLine && longLine > 0) return (int)longLine;
                }
                catch { }
            }
            return 0;
        }

        private static string BuildMessage(
            string scriptName,
            string eventName,
            Exception exception,
            out int line,
            out string scriptMessage)
        {
            line = FindLine(exception);
            scriptMessage = CleanMessage(exception?.Message, line);
            string location = line > 0 ? $" at line {line}" : string.Empty;
            return $"PGSL '{scriptName}' event '{eventName}' failed{location}: {scriptMessage}";
        }

        private static string CleanMessage(string message, int line)
        {
            message ??= "Unknown script failure.";
            if (line <= 0) return message;
            string prefix = $"Line {line}: ";
            return message.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? message[prefix.Length..]
                : message;
        }
    }
}
