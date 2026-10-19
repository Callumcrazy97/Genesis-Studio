#nullable disable
using System;
using Genesis.Shared.Scripting;

namespace Genesis.Runtime.Scripting;

// A game's text in several languages (GameText): Assets/Text/en.txt, fr.txt... with key = text lines.
public static partial class PgslCommands
{
    [PgslCommand("LocalText", "LocalText(key) -> string",
        "A line of the game's text in the chosen language (Assets/Text/<language>.txt, key = text lines); English or the first language when it has no such key, else the key itself", "Text")]
    public static string LocalText(string key) => GameText.Get(ProjectPath, key);

    [PgslCommand("LocalTextWith", "LocalTextWith(key, a, b?, c?) -> string",
        "LocalText with values put in for {0}, {1} and {2}: LocalTextWith(\"coins\", n) for \"coins = You have {0} coins\"", "Text")]
    public static string LocalTextWith(string key, object a, object b = null, object c = null) =>
        GameText.Fill(GameText.Get(ProjectPath, key), [a, b, c]);

    [PgslCommand("LocalTextHas", "LocalTextHas(key) -> bool", "True when the chosen language or the fallback has text for the key", "Text")]
    public static bool LocalTextHas(string key) => GameText.Has(ProjectPath, key);

    [PgslCommand("LanguageSet", "LanguageSet(language) -> bool",
        "Read the game's text in another language (\"fr\" for Assets/Text/fr.txt); false, keeping the current one, when there is no such file", "Text")]
    public static bool LanguageSet(string language) => GameText.SetLanguage(ProjectPath, language);

    [PgslCommand("LanguageGet", "LanguageGet() -> string",
        "The language the game's text is read in: the player's own when the game has it, else English, else the first there is", "Text")]
    public static string LanguageGet() => GameText.Language(ProjectPath);

    [PgslCommand("LanguageCount", "LanguageCount() -> number", "How many languages the game has text for", "Text")]
    public static double LanguageCount() => GameText.Languages(ProjectPath).Count;

    [PgslCommand("LanguageAt", "LanguageAt(index) -> string", "One of the game's languages by number (0 first, sorted), for a language menu; empty past the end", "Text")]
    public static string LanguageAt(double index)
    {
        var languages = GameText.Languages(ProjectPath);
        return double.IsFinite(index) && index >= 0 && index < languages.Count ? languages[(int)index] : "";
    }
}
