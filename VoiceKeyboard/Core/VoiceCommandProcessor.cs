namespace VoiceKeyboard.Core;

/// <summary>
/// Post-processes raw speech-model transcript into final text + a list of editing
/// actions to perform before/after text injection.
///
/// Rules:
///   • Voice commands are matched case-insensitively at word boundaries.
///   • Punctuation commands replace their spoken word with the symbol.
///   • Editing commands (undo, delete word…) produce no text but emit an Action.
///   • "caps on / caps off" toggles internal capitalisation state.
/// </summary>
public sealed class VoiceCommandProcessor
{
    private bool _capsOn;

    public ProcessedTranscript Process(string rawTranscript)
    {
        var actions = new List<TextAction>();
        string text = rawTranscript.Trim();

        // ── 1. Editing commands (consume entire transcript) ───────────────────
        var editAction = TryGetEditingCommand(text);
        if (editAction.HasValue)
        {
            return new ProcessedTranscript
            {
                TextToInsert = string.Empty,
                Actions = new[] { editAction.Value }
            };
        }

        // ── 2. Formatting commands (inline) ───────────────────────────────────
        if (text.Equals("caps on", StringComparison.OrdinalIgnoreCase))
        {
            _capsOn = true;
            return new ProcessedTranscript { TextToInsert = string.Empty, Actions = Array.Empty<TextAction>() };
        }
        if (text.Equals("caps off", StringComparison.OrdinalIgnoreCase))
        {
            _capsOn = false;
            return new ProcessedTranscript { TextToInsert = string.Empty, Actions = Array.Empty<TextAction>() };
        }

        // ── 3. Replace spoken punctuation & formatting words inline ──────────
        text = ReplacePunctuation(text);
        text = ReplaceFormatting(text);

        // ── 4. Apply caps ─────────────────────────────────────────────────────
        if (_capsOn)
            text = text.ToUpper();

        return new ProcessedTranscript
        {
            TextToInsert = text,
            Actions = actions
        };
    }

    // ── Editing commands ──────────────────────────────────────────────────────

    private static TextAction? TryGetEditingCommand(string text) =>
        text.ToLowerInvariant() switch
        {
            "undo"                 => TextAction.Undo,
            "delete last word"     => TextAction.DeleteLastWord,
            "delete last sentence" => TextAction.DeleteLastSentence,
            "select all"           => TextAction.SelectAll,
            "clear line"           => TextAction.ClearLine,
            _                      => null
        };

    // ── Punctuation replacement ───────────────────────────────────────────────

    private static readonly (string spoken, string symbol)[] PunctuationMap =
    {
        ("comma",          ","),
        ("period",         "."),
        ("full stop",      "."),
        ("question mark",  "?"),
        ("exclamation",    "!"),
        ("exclamation mark","!"),
        ("colon",          ":"),
        ("semicolon",      ";"),
        ("open bracket",   "("),
        ("close bracket",  ")"),
        ("open paren",     "("),
        ("close paren",    ")"),
        ("open quote",     "\""),
        ("close quote",    "\""),
        ("dash",           " — "),
        ("hyphen",         "-"),
        ("slash",          "/"),
        ("backslash",      "\\"),
        ("at sign",        "@"),
        ("hashtag",        "#"),
        ("percent",        "%"),
        ("ampersand",      "&"),
        ("asterisk",       "*"),
        ("equals",         "="),
        ("plus",           "+"),
    };

    private static string ReplacePunctuation(string text)
    {
        foreach (var (spoken, symbol) in PunctuationMap)
        {
            // Replace whole-word match (case-insensitive)
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(spoken)}\b",
                symbol,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }

    // ── Formatting replacement (new line etc.) ────────────────────────────────

    private static readonly (string spoken, string replacement)[] FormattingMap =
    {
        ("new paragraph", "\n\n"),
        ("new line",      "\n"),
        ("tab",           "\t"),
    };

    private static string ReplaceFormatting(string text)
    {
        foreach (var (spoken, replacement) in FormattingMap)
        {
            text = System.Text.RegularExpressions.Regex.Replace(
                text,
                $@"\b{System.Text.RegularExpressions.Regex.Escape(spoken)}\b",
                replacement,
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }
        return text;
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class ProcessedTranscript
{
    public string TextToInsert { get; set; } = "";
    public IReadOnlyList<TextAction> Actions { get; set; } = Array.Empty<TextAction>();
}

public enum TextAction
{
    Undo,
    DeleteLastWord,
    DeleteLastSentence,
    SelectAll,
    ClearLine
}
