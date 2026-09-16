using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace TorPos.Core;

public enum DsfinvkColumnKind
{
    AlphaNumeric,
    Numeric
}

public sealed record DsfinvkColumn(
    string Name,
    DsfinvkColumnKind Kind,
    int MaxLength,
    int Accuracy);

public sealed record DsfinvkTable(
    string Name,
    string FileName,
    IReadOnlyList<DsfinvkColumn> Columns)
{
    public DsfinvkColumn Column(string name) =>
        Columns.FirstOrDefault(c => c.Name == name)
        ?? throw new InvalidOperationException($"DSFinV-K {Name}: keine Spalte {name}.");
}

/// <summary>
/// R131: the table and column layout is read from the official index.xml that
/// the BZSt publishes with DSFinV-K 2.4 (shipped unchanged next to the CSV
/// files). Column order, text lengths and numeric accuracy therefore come from
/// the binding description itself, not from a hand-copied list.
/// </summary>
public static class DsfinvkIndex
{
    public static IReadOnlyList<DsfinvkTable> Parse(string indexXml)
    {
        // The official file declares a DOCTYPE with an external DTD. It is
        // only needed by the import tool; resolving it here would mean
        // network or file access, so the declaration is ignored.
        // The official file starts with a UTF-8 byte order mark; decoded to a
        // string it is a leading U+FEFF that XmlReader rejects.
        using var reader = XmlReader.Create(
            new StringReader(indexXml.TrimStart('﻿')),
            new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore, XmlResolver = null });

        var document = XDocument.Load(reader);
        var tables = new List<DsfinvkTable>();

        foreach (var table in document.Descendants("Table"))
        {
            var columns = table
                .Element("VariableLength")!
                .Elements("VariableColumn")
                .Select(column =>
                {
                    var name = (string)column.Element("Name")!;
                    if (column.Element("Numeric") is { } numeric)
                        return new DsfinvkColumn(name, DsfinvkColumnKind.Numeric, 0, (int?)numeric.Element("Accuracy") ?? 0);
                    return new DsfinvkColumn(name, DsfinvkColumnKind.AlphaNumeric, (int?)column.Element("MaxLength") ?? 0, 0);
                })
                .ToArray();

            tables.Add(new DsfinvkTable((string)table.Element("Name")!, (string)table.Element("URL")!, columns));
        }

        return tables;
    }
}

/// <summary>A money amount in cents, written with two decimals.</summary>
public readonly record struct DsfinvkMoney(long Cents);

/// <summary>
/// R131: writes DSFinV-K CSV exactly as the official index.xml describes it:
/// UTF-8, ";" between columns, CR LF between records, text in double quotes,
/// "," as decimal symbol, first line holding the column names (Range From 2).
///
/// Every value is checked against its column: text within MaxLength, a number
/// where a number is declared, amounts with two decimals (DSFinV-K 4.1: "Die
/// Darstellung aller Beträge erfolgt mit zwei Dezimalstellen", even where the
/// field allows five), quantities with three. A value that does not fit is an
/// error, never silently cut, because a truncated fiscal record is a wrong one.
/// </summary>
public static class DsfinvkCsv
{
    public const string RecordDelimiter = "\r\n";

    public static string Header(DsfinvkTable table) =>
        string.Join(";", table.Columns.Select(c => c.Name));

    public static string Row(DsfinvkTable table, IReadOnlyDictionary<string, object?> values)
    {
        foreach (var key in values.Keys)
        {
            if (table.Columns.All(c => c.Name != key))
                throw new InvalidOperationException($"DSFinV-K {table.Name}: unbekannte Spalte {key}.");
        }

        return string.Join(";", table.Columns.Select(column =>
            Field(table, column, values.TryGetValue(column.Name, out var value) ? value : null)));
    }

    /// <summary>
    /// Line breaks and tabs inside a text would split the record; they become
    /// spaces. Nothing else about the text is changed.
    /// </summary>
    public static string Clean(string text) =>
        text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();

    /// <summary>For free texts only (article names, notes): cut to the column length.</summary>
    public static string Fit(string text, int maxLength)
    {
        var cleaned = Clean(text);
        return cleaned.Length <= maxLength ? cleaned : cleaned[..maxLength];
    }

    private static string Field(DsfinvkTable table, DsfinvkColumn column, object? value)
    {
        if (value is null)
            return "";

        if (column.Kind == DsfinvkColumnKind.AlphaNumeric)
        {
            if (value is not string text)
                throw new InvalidOperationException($"DSFinV-K {table.Name}.{column.Name}: Text erwartet, {value.GetType().Name} erhalten.");

            text = Clean(text);
            if (column.MaxLength > 0 && text.Length > column.MaxLength)
                throw new InvalidOperationException($"DSFinV-K {table.Name}.{column.Name}: {text.Length} Zeichen, erlaubt sind {column.MaxLength}.");

            return text.Length == 0 ? "" : "\"" + text.Replace("\"", "\"\"") + "\"";
        }

        return value switch
        {
            long number when column.Accuracy == 0 => number.ToString(CultureInfo.InvariantCulture),
            int number when column.Accuracy == 0 => number.ToString(CultureInfo.InvariantCulture),
            DsfinvkMoney money when column.Accuracy >= 2 => Decimal(money.Cents / 100m, 2),
            decimal number when column.Accuracy > 0 => Decimal(number, column.Accuracy),
            _ => throw new InvalidOperationException(
                $"DSFinV-K {table.Name}.{column.Name}: Wert vom Typ {value.GetType().Name} passt nicht zu Numeric mit {column.Accuracy} Dezimalstellen.")
        };
    }

    private static string Decimal(decimal value, int decimals) =>
        value.ToString("0." + new string('0', decimals), CultureInfo.InvariantCulture).Replace('.', ',');

    /// <summary>ISO 8601 / RFC 3339 with offset, 25 characters (field length 30).</summary>
    public static string Timestamp(DateTimeOffset value) =>
        value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

    /// <summary>TSE log time as DSFinV-K Anhang E demands: "YYYY-MM-DDThh:mm:ss.fffZ".</summary>
    public static string TseTime(DateTimeOffset value) =>
        value.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
