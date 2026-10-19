using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace Genesis.Runtime.Scripting
{
    /// <summary>
    /// A game's text in several languages: one file per language in the project's
    /// <c>Assets/Text</c> folder (<c>en.txt</c>, <c>fr.txt</c>...), each line <c>key = text</c>.
    /// A key missing from the chosen language falls back to English (or the first language there
    /// is), then to the key itself, so an untranslated line shows as its key instead of nothing.
    /// </summary>
    public static class GameText
    {
        private static readonly object Gate = new();
        private static string _project = "";
        private static string _language = "";
        private static Dictionary<string, string> _chosen = new(StringComparer.Ordinal);
        private static Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
        private static bool _loaded;

        public static string Folder(string project) => Path.Combine(project ?? "", "Assets", "Text");

        /// <summary>The languages the project has text for (file names without .txt), sorted.</summary>
        public static List<string> Languages(string project)
        {
            var languages = new List<string>();
            try
            {
                string folder = Folder(project);
                if (Directory.Exists(folder))
                    foreach (string file in Directory.GetFiles(folder, "*.txt"))
                        languages.Add(Path.GetFileNameWithoutExtension(file));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            languages.Sort(StringComparer.OrdinalIgnoreCase);
            return languages;
        }

        /// <summary>The language text is read in now; chosen on first use when none was set.</summary>
        public static string Language(string project)
        {
            lock (Gate) { EnsureLoaded(project); return _language; }
        }

        /// <summary>Reads text in another language; false (keeping the current one) when the project has none in it.</summary>
        public static bool SetLanguage(string project, string language)
        {
            if (string.IsNullOrWhiteSpace(language)) return false;
            string code = language.Trim();
            lock (Gate)
            {
                EnsureLoaded(project);
                string match = Languages(project).Find(each => string.Equals(each, code, StringComparison.OrdinalIgnoreCase));
                if (match == null) return false;
                _language = match;
                _chosen = Read(project, match);
                return true;
            }
        }

        public static bool Has(string project, string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            lock (Gate) { EnsureLoaded(project); return _chosen.ContainsKey(key) || _fallback.ContainsKey(key); }
        }

        /// <summary>The text for a key in the chosen language, else the fallback language, else the key.</summary>
        public static string Get(string project, string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            lock (Gate)
            {
                EnsureLoaded(project);
                return _chosen.TryGetValue(key, out string text) || _fallback.TryGetValue(key, out text) ? text : key;
            }
        }

        /// <summary>Forgets every table read (a new game, or files edited while it runs).</summary>
        public static void Reset()
        {
            lock (Gate) { _loaded = false; _project = ""; _language = ""; _chosen = new(StringComparer.Ordinal); _fallback = new(StringComparer.Ordinal); }
        }

        private static void EnsureLoaded(string project)
        {
            project ??= "";
            if (_loaded && string.Equals(_project, project, StringComparison.OrdinalIgnoreCase)) return;
            _loaded = true;
            _project = project;
            List<string> languages = Languages(project);
            string fallback = languages.Find(each => each.Equals("en", StringComparison.OrdinalIgnoreCase))
                ?? (languages.Count > 0 ? languages[0] : "");
            _fallback = fallback.Length > 0 ? Read(project, fallback) : new Dictionary<string, string>(StringComparer.Ordinal);
            // The player's own language when the game has it, else the fallback.
            string system = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            _language = languages.Find(each => each.Equals(system, StringComparison.OrdinalIgnoreCase)) ?? fallback;
            _chosen = _language.Length > 0 && !_language.Equals(fallback, StringComparison.OrdinalIgnoreCase)
                ? Read(project, _language) : _fallback;
        }

        private static Dictionary<string, string> Read(string project, string language)
        {
            try { return Parse(File.ReadAllText(Path.Combine(Folder(project), language + ".txt"), Encoding.UTF8)); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new Dictionary<string, string>(StringComparer.Ordinal); }
        }

        /// <summary>
        /// <c>key = text</c> lines; blank lines and lines starting with <c>#</c> are skipped;
        /// <c>\n</c>, <c>\t</c> and <c>\\</c> in the text are a new line, a tab and a backslash.
        /// A key given twice keeps its last text.
        /// </summary>
        public static Dictionary<string, string> Parse(string text)
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            if (string.IsNullOrEmpty(text)) return table;
            foreach (string rawLine in text.Split('\n'))
            {
                string line = rawLine.TrimEnd('\r');
                string trimmed = line.TrimStart();
                if (trimmed.Length == 0 || trimmed[0] == '#') continue;
                int equals = line.IndexOf('=');
                if (equals <= 0) continue;
                string key = line[..equals].Trim();
                if (key.Length == 0) continue;
                table[key] = Unescape(line[(equals + 1)..].Trim());
            }
            return table;
        }

        private static string Unescape(string value)
        {
            if (value.IndexOf('\\') < 0) return value;
            var text = new StringBuilder(value.Length);
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                if (c == '\\' && i + 1 < value.Length)
                {
                    char next = value[i + 1];
                    if (next == 'n') { text.Append('\n'); i++; continue; }
                    if (next == 't') { text.Append('\t'); i++; continue; }
                    if (next == '\\') { text.Append('\\'); i++; continue; }
                }
                text.Append(c);
            }
            return text.ToString();
        }

        /// <summary>Puts values in for <c>{0}</c>, <c>{1}</c> and <c>{2}</c>; whole numbers without a decimal point.</summary>
        public static string Fill(string text, object[] values)
        {
            if (string.IsNullOrEmpty(text) || values == null || values.Length == 0 || text.IndexOf('{') < 0) return text ?? "";
            var result = new StringBuilder(text.Length + 16);
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '{' && i + 2 < text.Length && text[i + 2] == '}' && text[i + 1] >= '0' && text[i + 1] <= '9')
                {
                    int index = text[i + 1] - '0';
                    if (index < values.Length && values[index] != null)
                    {
                        result.Append(values[index] switch
                        {
                            double number when number == Math.Floor(number) && Math.Abs(number) < 1e15 => ((long)number).ToString(CultureInfo.InvariantCulture),
                            double number => number.ToString(CultureInfo.InvariantCulture),
                            bool flag => flag ? "true" : "false",
                            var other => Convert.ToString(other, CultureInfo.InvariantCulture),
                        });
                        i += 2;
                        continue;
                    }
                }
                result.Append(text[i]);
            }
            return result.ToString();
        }
    }
}
