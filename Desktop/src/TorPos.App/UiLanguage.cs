using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.LogicalTree;

namespace TorPos.App;

/// <summary>
/// Operator-interface language. R54 reduced this to a German-only facade; this
/// restores the DE/TR/EN selection the roadmap asks for.
///
/// Two rules shape the design:
///
/// 1. The German text stays in the windows and is the lookup key. Nothing has
///    to be re-keyed to resource identifiers, and an untranslated string simply
///    stays German instead of showing a missing-key placeholder.
/// 2. Only the operator interface is translated. Receipts, DSFinV-K exports,
///    Z-Berichte, TSE process data and the audit log are German fiscal
///    documents and are produced in Core/Infrastructure, which must never
///    reference this class. A safety check enforces that boundary.
/// </summary>
public static class UiLanguage
{
    public const string German = "DE";

    private static readonly string[] Supported = [German, "TR", "EN"];

    // What this class last did to a control: the German source it translated
    // from, and the text it actually rendered. Both are needed. Without the
    // source, a second Apply() would translate an already translated string and
    // a switch back to German could not restore the original. Without the
    // rendered value, a label the window changes at runtime - PFAND becoming
    // EXTRA on the Gastro till - would be overwritten again with the stale
    // source on the next Apply().
    private sealed class Rendered
    {
        public string Source = "";
        public string Text = "";
    }

    private static readonly ConditionalWeakTable<object, Rendered> Sources = new();

    private static string _current = German;

    public static string Current => _current;

    public static IReadOnlyList<string> Available => Supported;

    public static bool IsSupported(string? code) =>
        Normalize(code) is not null;

    public static void Set(string? code)
    {
        _current = Normalize(code) ?? German;
    }

    /// <summary>
    /// Translates one operator-interface string. Unknown text stays German.
    /// </summary>
    public static string T(string? german)
    {
        if (string.IsNullOrEmpty(german)) return german ?? "";
        if (_current == German) return german;

        var table = UiTranslations.For(_current);
        if (table.TryGetValue(german, out var translated) && translated.Length > 0)
            return translated;

        return Compound(german, table);
    }

    // A till label is often a label glued to a value - "GESAMT: 12,50 €",
    // "KARTENZAHLUNG · 12,50 €" - or two lines in one control, "BAR\nF1".
    // The whole string is never in the table, because the amount changes with
    // every sale. So when the exact lookup misses, the string is split at these
    // separators and each piece is translated on its own. The label becomes
    // Turkish; the amount, which is not interface text, is left exactly as the
    // window formatted it.
    private static readonly string[] Separators = ["\r\n", "\n", ": ", " · "];

    private static string Compound(string text, IReadOnlyDictionary<string, string> table)
    {
        var at = -1;
        var separator = "";
        foreach (var candidate in Separators)
        {
            var index = text.IndexOf(candidate, StringComparison.Ordinal);
            if (index < 0 || (at >= 0 && index >= at)) continue;
            at = index;
            separator = candidate;
        }

        if (at < 0) return text;

        return Piece(text[..at], table) + separator + Piece(text[(at + separator.Length)..], table);
    }

    // Each piece gets the same treatment as the whole: an exact entry wins, and
    // anything still compound is split again. Both pieces are shorter than what
    // they came from, so this ends.
    private static string Piece(string text, IReadOnlyDictionary<string, string> table) =>
        table.TryGetValue(text, out var translated) && translated.Length > 0
            ? translated
            : Compound(text, table);

    /// <summary>
    /// Re-renders a window in the current language. Called from every window's
    /// Opened handler and again when the language setting changes.
    /// </summary>
    public static void Apply(Control root)
    {
        if (root is null) return;

        if (root is Window window)
            window.Title = Translate(window, window.Title);

        foreach (var control in root.GetLogicalDescendants().OfType<Control>())
            ApplyTo(control);

        ApplyTo(root);
    }

    private static void ApplyTo(Control control)
    {
        switch (control)
        {
            case TextBlock text:
                text.Text = Translate(text, text.Text);
                break;

            case TextBox box:
                // Only the hint is interface text; the value the operator typed
                // is data and is never touched.
                box.PlaceholderText = Translate(box, box.PlaceholderText);
                break;

            case ContentControl content when content.Content is string literal:
                content.Content = Translate(content, literal);
                break;
        }
    }

    private static string? Translate(object owner, string? current)
    {
        if (string.IsNullOrEmpty(current)) return current;

        // The first sighting of a control records its German source; later calls
        // always translate from that source, never from a translated value.
        var entry = Sources.GetOrCreateValue(owner);

        // Anything other than the text we rendered last time means the window
        // set it itself, so that value becomes the new source.
        if (entry.Source.Length == 0 || !string.Equals(current, entry.Text, StringComparison.Ordinal))
            entry.Source = current;

        entry.Text = T(entry.Source);
        return entry.Text;
    }

    private static string? Normalize(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return Supported.Contains(normalized) ? normalized : null;
    }
}
