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

    // The original German text of every control this class has touched. Without
    // it a second Apply() would try to translate an already translated string
    // and a switch back to German could not restore the source text.
    private static readonly ConditionalWeakTable<object, string> Sources = new();

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
        return table.TryGetValue(german, out var translated) && translated.Length > 0
            ? translated
            : german;
    }

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
        var source = Sources.TryGetValue(owner, out var remembered) ? remembered : current;
        Sources.AddOrUpdate(owner, source);

        return T(source);
    }

    private static string? Normalize(string? code)
    {
        var normalized = code?.Trim().ToUpperInvariant();
        return Supported.Contains(normalized) ? normalized : null;
    }
}
