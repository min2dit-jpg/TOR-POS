namespace TorPos.Infrastructure;

/// <summary>
/// Text cells of the CSV files TOR writes for people to open in Excel/LibreOffice
/// (Artikel, Verkäufe, Audit-Log). A text that starts with = + - @, a tab or a
/// carriage return is read as a formula there (CSV/formula injection, OWASP):
/// an article or operator name like <c>=HYPERLINK(...)</c> would run on the
/// tax adviser's PC. Such a cell gets a leading apostrophe; the import strips it
/// again, so an export/import round-trip keeps the text. DSFinV-K and DATEV
/// files keep their prescribed format and do not use this.
/// </summary>
internal static class CsvCells
{
    private static bool StartsLikeFormula(string value) =>
        value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    public static string Text(string? value)
    {
        var text = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        if (StartsLikeFormula(text))
            text = "'" + text;
        return "\"" + text.Replace("\"", "\"\"") + "\"";
    }

    /// <summary>Undoes <see cref="Text"/>'s formula guard when a TOR export is imported again.</summary>
    public static string Unguard(string cell) =>
        cell.Length > 1 && cell[0] == '\'' && StartsLikeFormula(cell[1..]) ? cell[1..] : cell;
}
