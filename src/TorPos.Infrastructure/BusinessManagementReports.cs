using System.Globalization;
using System.Text;
using Microsoft.Data.Sqlite;
using TorPos.Core;

namespace TorPos.Infrastructure;

public sealed partial class BusinessManagementService
{
    public async Task<IReadOnlyList<ReportDocument>> BuildCurrentReportBundleAsync(CancellationToken ct = default)
    {
        var reports = new List<ReportDocument>
        {
            await BuildTurnoverSummaryAsync(ct),
            await BuildXReportAsync(ct),
            await BuildCashJournalAsync(ct),
            await BuildMonthlyTurnoverAsync(ct),
            await BuildSalesStatisticsAsync(ct),
            await BuildOperatorSettlementAsync(ct),
            await BuildStornoReportAsync(ct),
            await BuildInventoryReportAsync(ct),
            await BuildZArchiveSummaryAsync(ct)
        };
        return reports;
    }

    public async Task<ReportDocument> BuildZArchiveSummaryAsync(CancellationToken ct = default)
    {
        return await IoQueue.RunAsync(async () =>
        {
            var lines = new List<string>
            {
                "Archivierte Z-Berichte · nur vorhandene Archivdaten, es wird KEIN neuer Z-Bericht erzeugt.",
                "",
                "Z-Nr. | Zeitpunkt | Zeitraum | Bons | Listenwert | Angebot | Man.Rabatt | Umsatz | Bar | Karte | Bediener | Fiskalstatus"
            };
            await using var c = _db.OpenConnection();
            await using var q = c.CreateCommand();
            q.CommandText = """
                SELECT z_number,created_at,period_from,period_to,receipt_count,
                       list_gross_cents,promotion_discount_cents,manual_discount_cents,
                       gross_cents,cash_cents,card_cents,operator_name,fiscal_status
                FROM z_report_archive
                ORDER BY z_number DESC
                LIMIT 1000;
                """;
            await using var r = await q.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                lines.Add($"{r.GetInt64(0):000000} | {DateTimeOffset.Parse(r.GetString(1)):dd.MM.yyyy HH:mm} | " +
                          $"{DateTimeOffset.Parse(r.GetString(2)):dd.MM.yyyy HH:mm}–{DateTimeOffset.Parse(r.GetString(3)):dd.MM.yyyy HH:mm} | " +
                          $"{r.GetInt32(4)} | {Money(r.GetInt64(5))} | -{Money(r.GetInt64(6))} | -{Money(r.GetInt64(7))} | " +
                          $"{Money(r.GetInt64(8))} | {Money(r.GetInt64(9))} | {Money(r.GetInt64(10))} | {r.GetString(11)} | {r.GetString(12)}");
            }
            return new ReportDocument("Z-ARCHIV", lines, DateTimeOffset.Now);
        });
    }

    public async Task<IReadOnlyList<ReportDocument>> BuildMonthlyReportBundleAsync(int year, int month, CancellationToken ct = default)
    {
        if (year is < 2000 or > 2200 || month is < 1 or > 12)
            throw new ArgumentOutOfRangeException(nameof(month), "Ungültiger Berichtsmonat.");

        var fromLocal = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var toLocal = fromLocal.AddMonths(1);
        var from = new DateTimeOffset(fromLocal, TimeZoneInfo.Local.GetUtcOffset(fromLocal));
        var to = new DateTimeOffset(toLocal, TimeZoneInfo.Local.GetUtcOffset(toLocal));
        var fromUtcText = ToUtcColumnText(from);
        var toUtcText = ToUtcColumnText(to);
        var ym = $"{year:0000}-{month:00}";

        return await IoQueue.RunAsync(async () =>
        {
            var docs = new List<ReportDocument>();
            await using var c = _db.OpenConnection();

            // 1) Monatsübersicht - R71: Listenwert, Angebot und manueller Rabatt getrennt.
            var monthPeriod = await GetPeriodSummaryAsync(
                from,
                to.AddTicks(-1),
                ct);

            static string PromotionDate(string value)
            {
                return DateOnly.TryParseExact(
                    value,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var day)
                        ? day.ToString("dd.MM.yyyy")
                        : value;
            }

            var monthLines = new List<string>
            {
                $"Zeitraum: {fromLocal:dd.MM.yyyy} bis {toLocal.AddDays(-1):dd.MM.yyyy}",
                $"Bons: {monthPeriod.ReceiptCount}",
                "",
                "UMSATZ / RABATTE",
                $"Listenwert vor Angebot/Rabatt: {Money(monthPeriod.ListGrossCents)}",
                $"- Angebote / Aktionen: -{Money(monthPeriod.PromotionDiscountCents)}",
                $"- Manuelle Rabatte: -{Money(monthPeriod.ManualDiscountCents)}",
                $"= Umsatz nach Rabatt (brutto): {Money(monthPeriod.SalesGrossAfterDiscountCents)}",
                $"Belegstorno / Gegenbuchung: -{Money(monthPeriod.StornoCents)}",
                $"Retouren: -{Money(monthPeriod.ReturnCents)}",
                $"= Umsatz nach Storno/Retouren: {Money(monthPeriod.GrossCents)}",
                "",
                "ZAHLARTEN",
                $"Bar: {Money(monthPeriod.CashCents)}",
                $"Karte: {Money(monthPeriod.CardCents)}",
                "",
                "UMSATZSTEUER NACH RABATT"
            };

            foreach (var tax in monthPeriod.Taxes)
            {
                monthLines.Add(
                    GermanFormat.Number(tax.Rate, "0.##") + $" % · Brutto {Money(tax.GrossCents)} · " +
                    $"Netto {Money(tax.NetCents)} · Steuer {Money(tax.TaxCents)}");
            }

            if (monthPeriod.Promotions.Count > 0)
            {
                monthLines.Add("");
                monthLines.Add("ANGEBOTE / AKTIONEN");

                foreach (var promotion in monthPeriod.Promotions)
                {
                    monthLines.Add(
                        $"{promotion.Name} · -{promotion.Percent}% · " +
                        $"{PromotionDate(promotion.StartDate)}–{PromotionDate(promotion.EndDate)} · " +
                        $"Bons {promotion.ReceiptCount} · Rabatt -{Money(promotion.DiscountCents)} · " +
                        $"Umsatz {Money(promotion.SalesGrossCents)}");
                }
            }

            monthLines.Add("");
            monthLines.Add(
                "Dieser Bericht erzeugt keinen Z-Bericht und keinen Kassenabschluss.");

            docs.Add(new ReportDocument(
                $"MONATSÜBERSICHT {ym}",
                monthLines,
                DateTimeOffset.Now));

            // 2) Kassenjournal des Monats
            var journal = new List<string> { $"Zeitraum: {ym}", "", "BONS" };
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT s.receipt_number,s.created_at,s.payment_method,s.total_cents,COALESCE(o.operator_name,'')
                    FROM sales s LEFT JOIN sale_operators o ON o.sale_id=s.id
                    WHERE s.created_at_utc >= $from AND s.created_at_utc < $to
                    ORDER BY s.receipt_number;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    journal.Add($"Bon {r.GetInt64(0):000000} | {DateTimeOffset.Parse(r.GetString(1)):dd.MM.yyyy HH:mm} | {r.GetString(2)} | {Money(r.GetInt64(3))} | {r.GetString(4)}");
            }
            journal.Add(""); journal.Add("KASSENBEWEGUNGEN");
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT created_at,movement_type,amount_cents,reason,actor
                    FROM cash_movements WHERE created_at_utc >= $from AND created_at_utc < $to ORDER BY id;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                    journal.Add($"{DateTimeOffset.Parse(r.GetString(0)):dd.MM.yyyy HH:mm} | {r.GetString(1)} | {Money(r.GetInt64(2))} | {r.GetString(3)} | {r.GetString(4)}");
            }
            docs.Add(new ReportDocument($"KASSENJOURNAL {ym}", journal, DateTimeOffset.Now));

            // 3) Verkaufsstatistik
            // R91: same fix as BuildSalesStatisticsAsync - exclude BON
            // STORNO/Teilretoure sale_items rows, which otherwise counted a
            // returned product's quantity/revenue a second time.
            var salesStats = new List<string> { $"Zeitraum: {ym}", "", "Artikel | Menge | Umsatz" };
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT i.product_name,COALESCE(SUM(i.quantity),0),COALESCE(SUM(i.line_total_cents),0)
                    FROM sale_items i JOIN sales s ON s.id=i.sale_id
                    WHERE COALESCE(s.transaction_type,'SALE')='SALE'
                      AND s.created_at_utc >= $from AND s.created_at_utc < $to
                    GROUP BY i.product_name ORDER BY SUM(i.line_total_cents) DESC;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) salesStats.Add(GermanFormat.Line($"{r.GetString(0)} | {r.GetDouble(1):0.###} | {Money(r.GetInt64(2))}"));
            }
            docs.Add(new ReportDocument($"VERKAUFSSTATISTIK {ym}", salesStats, DateTimeOffset.Now));

            // 4) Bedienerabrechnung
            // R90: same fix as BuildOperatorSettlementAsync - excludes
            // STORNO/RETURN from Umsatz/Bar/Karte (previously silently added
            // on top instead of being excluded) and lists them separately,
            // now attributable since RecordStornoAsync/RecordReturnAsync
            // write a sale_operators row for the reversal too.
            var operators = new List<string> { $"Zeitraum: {ym}", "", "Bediener | Bons | Umsatz | Bar | Karte" };
            var operatorSaleRows = 0;
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT COALESCE(o.operator_name,'UNBEKANNT'),COUNT(*),COALESCE(SUM(s.total_cents),0),
                           COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.cash_portion_cents
                                             WHEN s.payment_method='CASH' THEN s.total_cents ELSE 0 END),0),
                           COALESCE(SUM(CASE WHEN s.cash_portion_cents<>0 OR s.card_portion_cents<>0 THEN s.card_portion_cents
                                             WHEN s.payment_method='CARD' THEN s.total_cents ELSE 0 END),0)
                    FROM sales s LEFT JOIN sale_operators o ON o.sale_id=s.id
                    WHERE COALESCE(s.transaction_type,'SALE')='SALE'
                      AND s.created_at_utc >= $from AND s.created_at_utc < $to
                    GROUP BY COALESCE(o.operator_name,'UNBEKANNT') ORDER BY 3 DESC;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    operatorSaleRows++;
                    operators.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {Money(r.GetInt64(2))} | {Money(r.GetInt64(3))} | {Money(r.GetInt64(4))}");
                }
            }
            if (operatorSaleRows == 0) operators.Add("(keine)");
            operators.Add(""); operators.Add("STORNO/RETOURE NACH BEARBEITER (nicht im Umsatz oben enthalten)");
            operators.Add("Bearbeiter | Anzahl | Betrag");
            var operatorReversalRows = 0;
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT COALESCE(o.operator_name,'UNBEKANNT'),COUNT(*),COALESCE(SUM(s.total_cents),0)
                    FROM sales s LEFT JOIN sale_operators o ON o.sale_id=s.id
                    WHERE s.transaction_type IN ('STORNO','RETURN')
                      AND s.created_at_utc >= $from AND s.created_at_utc < $to
                    GROUP BY COALESCE(o.operator_name,'UNBEKANNT') ORDER BY 3 DESC;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    operatorReversalRows++;
                    operators.Add($"{r.GetString(0)} | {r.GetInt64(1)} | {Money(r.GetInt64(2))}");
                }
            }
            if (operatorReversalRows == 0) operators.Add("(keine)");
            docs.Add(new ReportDocument($"BEDIENERABRECHNUNG {ym}", operators, DateTimeOffset.Now));

            // 5) Storno- und Retourenjournal.
            // R88: same fix as BuildStornoReportAsync - the previous
            // `event_type LIKE '%STORNO%'` on audit_log missed SOFORT_STORNO
            // (a different table, pos_action_log) and R82's Teilretoure
            // (logged as event_type 'SALE_RETURN', never matched by that
            // LIKE pattern). Pull each kind from its real source instead.
            var storno = new List<string> { $"Zeitraum: {ym}", "", "SOFORT STORNO", "Zeit | Benutzer | Details | Betrag" };
            var sofortCount = 0; long sofortCents = 0;
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT created_at,actor,details,amount_cents FROM pos_action_log
                    WHERE action_type='SOFORT_STORNO' AND phase='APPLIED'
                      AND created_at_utc >= $from AND created_at_utc < $to
                    ORDER BY id;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    sofortCount++; sofortCents += r.GetInt64(3);
                    storno.Add($"{r.GetString(0)} | {r.GetString(1)} | {r.GetString(2)} | {Money(r.GetInt64(3))}");
                }
            }
            if (sofortCount == 0) storno.Add("(keine)");
            storno.Add(""); storno.Add($"Summe SOFORT STORNO: {sofortCount} · {Money(sofortCents)}"); storno.Add("");
            storno.Add("BON STORNO / TEILRETOURE");
            storno.Add("Zeit | Bon-Nr. | Referenz-Bon | Art | Bediener | Betrag");
            var reversalCount = 0; long reversalCents = 0;
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT s.created_at,s.receipt_number,COALESCE(orig.receipt_number,0),
                           s.transaction_type,COALESCE(o.operator_name,''),s.total_cents
                    FROM sales s
                    LEFT JOIN sale_operators o ON o.sale_id=s.id
                    LEFT JOIN sales orig ON orig.id=s.original_sale_id
                    WHERE s.transaction_type IN ('STORNO','RETURN')
                      AND s.created_at_utc >= $from AND s.created_at_utc < $to
                    ORDER BY s.id;
                    """;
                q.Parameters.AddWithValue("$from", fromUtcText); q.Parameters.AddWithValue("$to", toUtcText);
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct))
                {
                    reversalCount++; reversalCents += r.GetInt64(5);
                    var kind = r.GetString(3) == "STORNO" ? "BON STORNO" : "TEILRETOURE";
                    storno.Add(
                        $"{r.GetString(0)} | {r.GetInt64(1):000000} | {r.GetInt64(2):000000} | " +
                        $"{kind} | {r.GetString(4)} | {Money(r.GetInt64(5))}");
                }
            }
            if (reversalCount == 0) storno.Add("(keine)");
            storno.Add(""); storno.Add($"Summe BON STORNO/TEILRETOURE: {reversalCount} · {Money(reversalCents)}");
            docs.Add(new ReportDocument($"STORNO- UND RETOURENJOURNAL {ym}", storno, DateTimeOffset.Now));

            // 6) Vorhandene Z-Berichte im Monat, ohne einen neuen Abschluss zu erzeugen.
            var z = new List<string> { $"Zeitraum: {ym}", "Kein neuer Z-Bericht wird erzeugt.", "", "Z-Nr. | Zeitpunkt | Bons | Listenwert | Angebot | Man.Rabatt | Umsatz | Bar | Karte | Bediener | Fiskalstatus" };
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT z_number,created_at,receipt_count,
                           list_gross_cents,promotion_discount_cents,manual_discount_cents,
                           gross_cents,cash_cents,card_cents,operator_name,fiscal_status
                    FROM z_report_archive WHERE created_at >= $from AND created_at < $to ORDER BY z_number;
                    """;
                q.Parameters.AddWithValue("$from", from.ToString("O")); q.Parameters.AddWithValue("$to", to.ToString("O"));
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) z.Add(
                    $"{r.GetInt64(0):000000} | {DateTimeOffset.Parse(r.GetString(1)):dd.MM.yyyy HH:mm} | {r.GetInt32(2)} | " +
                    $"{Money(r.GetInt64(3))} | -{Money(r.GetInt64(4))} | -{Money(r.GetInt64(5))} | " +
                    $"{Money(r.GetInt64(6))} | {Money(r.GetInt64(7))} | {Money(r.GetInt64(8))} | {r.GetString(9)} | {r.GetString(10)}");
            }
            docs.Add(new ReportDocument($"Z-ARCHIV {ym}", z, DateTimeOffset.Now));

            // 7) Warenbestand at send/export time. Historical inventory snapshots are not invented.
            var inventory = new List<string>
            {
                $"Monatspaket: {ym}",
                $"Bestandsstand zum Erstellungszeitpunkt: {DateTime.Now:dd.MM.yyyy HH:mm}",
                "Hinweis: TOR speichert derzeit keinen rückwirkenden Monatsend-Bestands-Snapshot; dieser PDF zeigt den aktuellen Bestand.",
                "",
                "Artikel | EAN | Bestand | Min. | VK | EK | Gruppe / Warengruppe"
            };
            await using (var q = c.CreateCommand())
            {
                q.CommandText = """
                    SELECT p.name,p.barcode,COALESCE(p.stock_quantity,0),COALESCE(p.min_stock_quantity,0),
                           p.base_price_cents,COALESCE(p.purchase_price_cents,0),COALESCE(g.name,'Standard'),c.name,p.unit
                    FROM products p JOIN categories c ON c.id=p.category_id
                    LEFT JOIN category_master_data m ON m.category_id=c.id
                    LEFT JOIN product_groups g ON g.id=m.group_id
                    WHERE p.is_active=1 ORDER BY COALESCE(g.sort_order,0),g.name,c.sort_order,c.name,p.sort_order,p.name;
                    """;
                await using var r = await q.ExecuteReaderAsync(ct);
                while (await r.ReadAsync(ct)) inventory.Add(GermanFormat.Line($"{r.GetString(0)} | {r.GetString(1)} | {r.GetDouble(2):0.###} {r.GetString(8)} | {r.GetDouble(3):0.###} | {Money(r.GetInt64(4))} | {Money(r.GetInt64(5))} | {r.GetString(6)} / {r.GetString(7)}"));
            }
            docs.Add(new ReportDocument($"WARENBESTAND {ym}", inventory, DateTimeOffset.Now));

            return (IReadOnlyList<ReportDocument>)docs;
        });
    }

    public string CreatePdfPackage(IReadOnlyList<ReportDocument> documents, string targetRoot, string packageName)
    {
        if (documents.Count == 0) throw new InvalidOperationException("Keine Berichte zum Exportieren vorhanden.");
        if (string.IsNullOrWhiteSpace(targetRoot)) throw new ArgumentException("Zielordner fehlt.", nameof(targetRoot));
        var folder = Path.Combine(targetRoot.Trim(), SafeFileName(packageName));
        Directory.CreateDirectory(folder);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in documents)
        {
            var baseName = SafeFileName(doc.Title);
            var name = baseName;
            var i = 2;
            while (!used.Add(name)) name = baseName + "-" + i++;
            var path = Path.Combine(folder, name + ".pdf");
            SimplePdfWriter.WriteTextReport(path, doc.Title, doc.Lines);
        }
        return folder;
    }

    public string SavePdf(ReportDocument document, string targetPath)
    {
        if (string.IsNullOrWhiteSpace(targetPath)) throw new ArgumentException("PDF-Ziel fehlt.", nameof(targetPath));
        if (!targetPath.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) targetPath += ".pdf";
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath) ?? Environment.CurrentDirectory);
        SimplePdfWriter.WriteTextReport(targetPath, document.Title, document.Lines);
        return targetPath;
    }
}
