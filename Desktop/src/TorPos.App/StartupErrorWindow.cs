using Avalonia.Controls;
using Avalonia.Media;

namespace TorPos.App;

public sealed class StartupErrorWindow : Window
{
    public StartupErrorWindow(string error, string logPath)
    {
        Title = "TOR POS – Startfehler";
        Width = 940;
        Height = 700;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var errorBox = new TextBox
        {
            Text = error,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 420
        };

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"),
            Margin = new Avalonia.Thickness(24),
            RowSpacing = 12,
            Children =
            {
                new TextBlock
                {
                    Text = "TOR POS konnte nicht gestartet werden.",
                    FontSize = 25,
                    FontWeight = FontWeight.Bold
                },
                new TextBlock
                {
                    Text = "Der Fehler wurde protokolliert. Bitte dieses Fenster oder die Logdatei an TOR Service senden.",
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = Brushes.Orange
                },
                errorBox,
                new TextBlock
                {
                    Text = $"Logdatei: {logPath}",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.70
                }
            }
        };

        Grid.SetRow(((Grid)Content).Children[1], 1);
        Grid.SetRow(errorBox, 2);
        Grid.SetRow(((Grid)Content).Children[3], 3);
    }
}
