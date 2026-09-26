// The printer question comes before the payment page. On the page the cashier
// enters GEGEBEN and usually already holds the customer's cash; a printer
// ABBRECHEN after it ended the payment with money taken and no sale booked
// (BAR and the cash part of GEMISCHT alike).
public static class PrinterBeforeCashTests
{
    public static void Run(Action<bool, string> assert)
    {
        var main = File.ReadAllText(FindRepoFile("Desktop/src/TorPos.App/MainWindow.axaml.cs"));
        string Body(string signature)
        {
            var start = main.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";
            var next = main.IndexOf("\n    private ", start + signature.Length, StringComparison.Ordinal);
            return next < 0 ? main[start..] : main[start..next];
        }

        var open = Body("private async Task OpenPaymentWindowAsync(");
        var checkout = Body("private async Task CheckoutAsync(");
        var printer = open.IndexOf("EnsureReceiptPrinterReadyForCheckoutAsync()", StringComparison.Ordinal);
        var page = open.IndexOf("new PaymentChoiceWindow(", StringComparison.Ordinal);
        var reset = open.LastIndexOf("_printerCheckedForPayment = false;", StringComparison.Ordinal);
        assert(
            printer > 0 && page > printer &&
            open.IndexOf("return;", printer, StringComparison.Ordinal) < page &&
            reset > page && open.IndexOf("finally", page, StringComparison.Ordinal) < reset &&
            checkout.Contains("_printerCheckedForPayment || await EnsureReceiptPrinterReadyForCheckoutAsync()", StringComparison.Ordinal),
            "Checkout asks about the receipt printer before the payment page where GEGEBEN is entered, not after the cash was taken; the answer holds for one payment attempt only");
    }

    private static string FindRepoFile(string relativePath)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, relativePath);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        throw new FileNotFoundException(relativePath);
    }
}
