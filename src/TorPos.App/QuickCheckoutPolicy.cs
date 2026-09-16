using TorPos.Core;

namespace TorPos.App;

public static class QuickCheckoutPolicy
{
    public static PaymentMethod? ResolveMethod(
        IReadOnlyDictionary<string, string> settings,
        bool cashEnabled,
        bool cardEnabled)
    {
        var configured = settings.GetText("pay.quick.default", "AUS")
            .Trim()
            .ToUpperInvariant();

        if (configured == "BAR" && cashEnabled)
            return PaymentMethod.Cash;

        if (configured == "KARTE" && cardEnabled)
            return PaymentMethod.Card;

        // If exactly one tender is active there is no useful reason to show
        // another tender-selection dialog.
        if (cashEnabled && !cardEnabled)
            return PaymentMethod.Cash;

        if (cardEnabled && !cashEnabled)
            return PaymentMethod.Card;

        return null;
    }

    public static bool UseExactCashWithoutDialog(
        IReadOnlyDictionary<string, string> settings,
        PaymentMethod method,
        bool invokedByQuickCheckout) =>
        invokedByQuickCheckout &&
        method == PaymentMethod.Cash &&
        settings.GetBool("pay.quick.cash_exact", false);
}
