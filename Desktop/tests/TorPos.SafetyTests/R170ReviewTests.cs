using TorPos.Core;

public static class R170ReviewTests
{
    public static Task Run(Action<bool, string> assert)
    {
        assert(
            WeightedSales.ToKilograms(500m, WeightInputUnit.Gram) == 0.500m &&
            WeightedSales.ToKilograms(1250m, WeightInputUnit.Gram) == 1.250m,
            "R170 gram input is normalized to exact kilogram quantities");

        assert(
            WeightedSales.ToKilograms(0.5m, WeightInputUnit.Kilogram) == 0.500m &&
            WeightedSales.TotalCents(1990, 0.500m) == 995,
            "R170 0.5 kg at 19.90 EUR/kg totals exactly 9.95 EUR");

        assert(
            new Product { Unit = "kg" }.IsWeighted &&
            !new Product { Unit = "Stück" }.IsWeighted &&
            !new Product { Unit = "g" }.IsWeighted,
            "R170 only explicit kg articles enter the weighed-sale flow so legacy units never change semantics accidentally");

        var engine = new SaleEngine();
        engine.Add(
            new Product
            {
                Id = 170,
                Name = "Baklava",
                Unit = "kg",
                BasePriceCents = 1990,
                VatRate = 7m
            },
            quantity: WeightedSales.ToKilograms(500m, WeightInputUnit.Gram));
        assert(
            engine.Cart.Count == 1 &&
            engine.Cart[0].IsWeighted &&
            engine.Cart[0].Unit == "kg" &&
            engine.Cart[0].Quantity == 0.500m &&
            engine.Cart[0].LineTotalCents == 995,
            "R170 SaleEngine carries kg as the commercial unit and uses decimal stock/sale quantity");

        var weightedVariantRejected = false;
        try
        {
            engine.Add(
                new Product { Id = 171, Name = "Gewicht", Unit = "kg", BasePriceCents = 1000 },
                new ProductVariant(1, 171, "Variante", 1000));
        }
        catch (InvalidOperationException)
        {
            weightedVariantRejected = true;
        }
        assert(
            weightedVariantRejected,
            "R170 weighted products fail closed for variants/menus instead of creating ambiguous per-piece pricing");

        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        assert(
            main.Contains("new WeightEntryWindow(p)", StringComparison.Ordinal) &&
            main.Contains("weighed.Kilograms", StringComparison.Ordinal) &&
            main.Contains("Gewichtsartikel: Gewicht über MENGE × ändern", StringComparison.Ordinal) &&
            main.Contains("await _promotions.GetBestForProductAsync", StringComparison.Ordinal) &&
            !main.Contains("if (p.IsWeighted)\n                    promotion = null;", StringComparison.Ordinal),
            "R170 cashier opens a gram/kg dialog and blocks dangerous +1/-1 kg shortcuts; R174 removes the temporary weighted-promotion suppression");

        var editor = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/ProductEditorWindow.cs"));
        assert(
            editor.Contains("Verkauf nach Gewicht (Gramm / Kilogramm) · Preis pro kg", StringComparison.Ordinal) &&
            editor.Contains("var weighted =", StringComparison.Ordinal) &&
            editor.Contains("? \"kg\"", StringComparison.Ordinal) &&
            editor.Contains("Gewichtsartikel dürfen keine Varianten oder Menü-/Combo-Bestandteile haben.", StringComparison.Ordinal),
            "R170 article editor explicitly configures weighed sales, forces kg storage and prevents incompatible menu/variant setups");

        var scale = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/ScaleSetupWindow.cs"));
        assert(
            scale.Contains("MANUELL · keine Verbindung", StringComparison.Ordinal) &&
            scale.Contains("SERIELL / USB-COM", StringComparison.Ordinal) &&
            scale.Contains("LAN / TCP", StringComparison.Ordinal) &&
            scale.Contains("WAAGEN-BARCODE / ETIKETT", StringComparison.Ordinal) &&
            scale.Contains("scale.manual_fallback", StringComparison.Ordinal),
            "R170 scale settings cover no-connection manual sales plus serial, LAN and scale-barcode preparation");

        assert(
            scale.Contains("aktiviert aber ohne freigegebenes Herstellerprotokoll keinen automatischen Gewichtsempfang", StringComparison.Ordinal) &&
            scale.Contains("Ein echter Live-Gerätetest wird erst mit dem freigegebenen Protokoll/Adapter durchgeführt.", StringComparison.Ordinal),
            "R170 scale setup does not falsely claim a live hardware integration from saved COM/IP settings alone");

        var settings = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/SettingsWindow.axaml.cs"));
        assert(
            settings.Contains("WAAGEN-EINSTELLUNGEN · MANUELL / COM / LAN / BARCODE", StringComparison.Ordinal) &&
            settings.Contains("Eine separate Waage ohne Kassenanschluss ist vollständig nutzbar", StringComparison.Ordinal),
            "R170 Geräte settings expose scale setup and explicitly support a completely disconnected shop scale");

        var printer = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/StarMcPrint3PrinterService.cs"));
        var digital = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Core/DigitalReceipt.cs"));
        assert(
            printer.Contains("WeightedSales.QuantityLabel(item.Quantity)", StringComparison.Ordinal) &&
            printer.Contains("/kg", StringComparison.Ordinal) &&
            digital.Contains("WeightedSales.QuantityLabel(line.Quantity)", StringComparison.Ordinal) &&
            digital.Contains("Preis pro kg", StringComparison.Ordinal),
            "R170 paper and digital receipts label weighed quantity and the per-kg price basis");

        var infrastructure = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/Infrastructure.cs"));
        var training = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/TrainingReceiptRepository.cs"));
        var orders = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/OrderBestellungRepository.cs"));
        var cancelled = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.Infrastructure/CancelledPositionStore.cs"));
        assert(
            infrastructure.Contains("p.unit FROM products p WHERE p.id=sale_items.product_id", StringComparison.Ordinal) &&
            infrastructure.Contains("p.unit FROM products p WHERE p.id=parked_receipt_items.product_id", StringComparison.Ordinal) &&
            training.Contains("p.unit FROM products p WHERE p.id=training_receipt_items.product_id", StringComparison.Ordinal) &&
            orders.Contains("p.unit FROM products p WHERE p.id=i.product_id", StringComparison.Ordinal) &&
            cancelled.Contains("p.unit FROM products p WHERE p.id={table}.product_id", StringComparison.Ordinal),
            "R170 sale, parked, training, order and cancelled-position reloads restore the kg unit for receipt/reversal flows");

        return Task.CompletedTask;
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(
                    dir.FullName,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException($"R170 review could not locate repository file: {relativePath}");
    }
}
