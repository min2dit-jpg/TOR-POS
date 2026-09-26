using System.Globalization;

namespace TorPos.Core;

/// <summary>
/// Stock and Mindestbestand are plain numbers in the article's unit. Changing
/// the unit of an article that already has a count (12 Stück → kg) would
/// silently turn it into 12 kg, so such a change needs both values counted
/// again in the new unit.
/// </summary>
public static class StockUnitRules
{
    public static bool Changes(string? oldUnit, string? newUnit) =>
        !string.Equals((oldUnit ?? "").Trim(), (newUnit ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    public static bool NeedsRecount(string? oldUnit, string? newUnit, decimal oldStock, decimal oldMinStock) =>
        Changes(oldUnit, newUnit) && (oldStock != 0m || oldMinStock != 0m);

    public static string RecountMessage(string? oldUnit, string? newUnit, decimal oldStock, decimal oldMinStock)
    {
        var de = CultureInfo.GetCultureInfo("de-DE");
        return $"Einheit von \"{(oldUnit ?? "").Trim()}\" auf \"{(newUnit ?? "").Trim()}\" geändert: Bestand {oldStock.ToString("0.###", de)} " +
               $"und Mindestbestand {oldMinStock.ToString("0.###", de)} wurden in \"{(oldUnit ?? "").Trim()}\" gezählt. " +
               $"Bitte Bestand und Mindestbestand in \"{(newUnit ?? "").Trim()}\" neu zählen und bestätigen.";
    }
}

public sealed class StockUnitChangeException(string message) : InvalidOperationException(message);
