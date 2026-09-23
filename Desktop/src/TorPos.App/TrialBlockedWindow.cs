using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed class TrialBlockedWindow : Window
{
    public TrialBlockedWindow(TrialLicenseStatus status)
    {
        Opened += (_, _) => UiLanguage.Apply(this);
        Title = "TOR POS Demo";
        Width = 680;
        Height = 390;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var heading = status.State switch
        {
            TrialLicenseState.Expired => "Die 7-Tage-Demo ist beendet.",
            TrialLicenseState.ClockRollback => "Demo-Zeitprüfung erforderlich.",
            _ => "Demo-Aktivierung erforderlich."
        };

        var detail = status.Message;
        if (status.ExpiresAtUtc is not null)
            detail += $"\n\nDemo-Ende: {status.ExpiresAtUtc.Value.ToLocalTime():dd.MM.yyyy HH:mm}";

        var close = new Button
        {
            Content = "BEENDEN",
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            MinWidth = 140,
            MinHeight = 48
        };
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto,*,Auto"),
            Margin = new Thickness(30),
            RowSpacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = "TOR POS · 7-TAGE-DEMO",
                    FontSize = 14,
                    FontWeight = FontWeight.Bold,
                    Foreground = Brushes.DodgerBlue
                },
                new TextBlock
                {
                    Text = heading,
                    FontSize = 28,
                    FontWeight = FontWeight.Bold,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = detail,
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                },
                new TextBlock
                {
                    Text = "Eine erneute Installation startet auf demselben PC keine neue Demo. " +
                           "Für die Vollversion: torpos.de",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Orange
                },
                close
            }
        };

        for (var i = 0; i < ((Grid)Content).Children.Count; i++)
            Grid.SetRow(((Grid)Content).Children[i], i);
    }
}
