using System.IO.Compression;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

/// <summary>
/// R171 post-export delivery assistant. The canonical DSFinV-K folder stays
/// untouched. USB receives the complete folder tree; e-mail receives one ZIP
/// containing exactly that export folder.
/// </summary>
public sealed class DsfinvkDeliveryWindow : Window
{
    private const long MaxMailZipBytes = 15L * 1024 * 1024;

    private readonly ISettingsRepository _settings;
    private readonly IAuditLog _audit;
    private readonly string _exportFolder;
    private readonly DateOnly _from;
    private readonly DateOnly _to;
    private readonly string _actor;

    private readonly ComboBox _usb = new()
    {
        MinHeight = 44,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private readonly TextBox _recipient = new()
    {
        MinHeight = 44,
        PlaceholderText = "z. B. steuerberater@kanzlei.de"
    };

    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 52
    };

    private string _steuerberater = "";
    private string _reportRecipient = "";

    public DsfinvkDeliveryWindow(
        ISettingsRepository settings,
        IAuditLog audit,
        string exportFolder,
        DateOnly from,
        DateOnly to,
        string actor)
    {
        _settings = settings;
        _audit = audit;
        _exportFolder = exportFolder;
        _from = from;
        _to = to;
        _actor = string.IsNullOrWhiteSpace(actor) ? "SYSTEM" : actor.Trim();

        if (!Directory.Exists(_exportFolder))
            throw new DirectoryNotFoundException(
                "DSFinV-K Exportordner wurde nicht gefunden: " + _exportFolder);

        Title = "TOR POS · DSFinV-K weitergeben";
        Width = 820;
        Height = 700;
        MinWidth = 700;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = AppTheme.BgPrimary;

        var usbRefresh = Button("USB-LAUFWERKE AKTUALISIEREN");
        usbRefresh.Click += (_, _) => RefreshUsb();

        var usbCopy = Button("AUF USB KOPIEREN");
        usbCopy.Background = AppTheme.SuccessGreen;
        usbCopy.BorderBrush = AppTheme.SuccessGreenBorder;
        usbCopy.Click += async (_, _) =>
        {
            if (_usb.SelectedItem is not DriveItem drive)
            {
                _status.Text = UiLanguage.T("Kein USB-Laufwerk ausgewählt. Alternativ ANDEREN ORDNER WÄHLEN benutzen.");
                return;
            }

            await CopyToAsync(
                Path.Combine(drive.Root, "TOR-POS-Pruefung"),
                "USB");
        };

        var chooseFolder = Button("ANDEREN USB-/ORDNER WÄHLEN");
        chooseFolder.Click += async (_, _) =>
        {
            var folders = await StorageProvider.OpenFolderPickerAsync(
                new Avalonia.Platform.Storage.FolderPickerOpenOptions
                {
                    Title = "USB-Laufwerk oder Zielordner auswählen",
                    AllowMultiple = false
                });
            var folder = folders.FirstOrDefault();
            if (folder is null)
            {
                _status.Text = UiLanguage.T("Kopiervorgang abgebrochen.");
                return;
            }

            await CopyToAsync(
                Path.Combine(folder.Path.LocalPath, "TOR-POS-Pruefung"),
                "Ordner");
        };

        var steuerberater = Button("STEUERBERATER-ADRESSE");
        steuerberater.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_steuerberater))
                _recipient.Text = _steuerberater;
            else
                _status.Text = UiLanguage.T("Unter DATEV ist noch keine Steuerberater-E-Mail gespeichert.");
        };

        var own = Button("GESPEICHERTE BERICHTS-E-MAIL");
        own.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(_reportRecipient))
                _recipient.Text = _reportRecipient;
            else
                _status.Text = UiLanguage.T("Unter Berichte & E-Mail ist noch keine Empfänger-Adresse gespeichert.");
        };

        var send = Button("DSFINV-K PER E-MAIL SENDEN");
        send.Background = AppTheme.InfoBlue;
        send.BorderBrush = AppTheme.InfoBlueBorder;
        send.Click += async (_, _) =>
        {
            send.IsEnabled = false;
            try
            {
                await SendEmailAsync();
            }
            finally
            {
                send.IsEnabled = true;
            }
        };

        var close = Button("FERTIG / SCHLIESSEN");
        close.Click += (_, _) => Close();

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Children =
            {
                new ScrollViewer
                {
                    VerticalScrollBarVisibility =
                        Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                    Content = new StackPanel
                    {
                        Margin = new Thickness(22),
                        Spacing = 16,
                        Children =
                        {
                            new TextBlock
                            {
                                Text = "DSFINV-K EXPORT IST FERTIG",
                                FontSize = 28,
                                FontWeight = FontWeight.Bold
                            },
                            new TextBlock
                            {
                                Text = $"{_from:dd.MM.yyyy}–{_to:dd.MM.yyyy}\n{_exportFolder}",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = AppTheme.AccentTeal
                            },
                            new TextBlock
                            {
                                Text = "Wie möchten Sie die Prüfungsdaten weitergeben? Der bereits erzeugte Original-Export bleibt unverändert erhalten.",
                                TextWrapping = TextWrapping.Wrap
                            },
                            Section(
                                "USB / externer Datenträger",
                                new TextBlock
                                {
                                    Text = "TOR kopiert den vollständigen DSFinV-K-Ordner mit allen CSV-, XML-, DTD- und Protokolldateien. Es wird nicht nur eine einzelne CSV kopiert.",
                                    TextWrapping = TextWrapping.Wrap,
                                    Opacity = 0.76
                                },
                                _usb,
                                new WrapPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    ItemSpacing = 10,
                                    LineSpacing = 10,
                                    Children = { usbRefresh, usbCopy, chooseFolder }
                                }),
                            Section(
                                "E-Mail · Steuerberater / eigene Adresse",
                                new TextBlock
                                {
                                    Text = "Für E-Mail packt TOR den vollständigen Export in eine ZIP-Datei und verwendet den unter Berichte & E-Mail aktiven Versandweg (TOR Mail, Google oder SMTP). Die Empfänger-Adresse kann für diesen Versand frei geändert werden.",
                                    TextWrapping = TextWrapping.Wrap,
                                    Opacity = 0.76
                                },
                                _recipient,
                                new WrapPanel
                                {
                                    Orientation = Orientation.Horizontal,
                                    ItemSpacing = 10,
                                    LineSpacing = 10,
                                    Children = { steuerberater, own, send }
                                }),
                            new Border
                            {
                                Background = AppTheme.SurfacePanel,
                                BorderBrush = AppTheme.PanelBorder,
                                BorderThickness = new Thickness(1),
                                CornerRadius = new CornerRadius(9),
                                Padding = new Thickness(12),
                                Child = _status
                            },
                            new TextBlock
                            {
                                Text = "E-Mail-Hinweis: Große Prüfdatensätze können Mail-Größenlimits überschreiten. TOR versendet deshalb keine ZIP-Datei über 15 MB; in diesem Fall USB/Datenträger verwenden.",
                                TextWrapping = TextWrapping.Wrap,
                                Foreground = AppTheme.WarningAmber
                            }
                        }
                    }
                },
                close
            }
        };

        Grid.SetRow(close, 1);
        close.Margin = new Thickness(22, 8, 22, 18);
        close.HorizontalAlignment = HorizontalAlignment.Right;

        Opened += async (_, _) =>
        {
            await LoadAsync();
            RefreshUsb();
            UiLanguage.Apply(this);
        };
    }

    private async Task LoadAsync()
    {
        var values = await _settings.LoadAllAsync();
        _steuerberater =
            values.GetValueOrDefault(DatevKassenbuchAsciiService.SettingRecipient, "").Trim();
        _reportRecipient =
            values.GetValueOrDefault("reports.email.recipient", "").Trim();

        _recipient.Text =
            !string.IsNullOrWhiteSpace(_steuerberater)
                ? _steuerberater
                : _reportRecipient;

        _status.Text = UiLanguage.T(
            "Export lokal gespeichert. USB kopieren oder E-Mail senden ist möglich.");
    }

    private void RefreshUsb()
    {
        var items = new List<DriveItem>();
        try
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Removable)
                        continue;

                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? "USB"
                        : drive.VolumeLabel;
                    items.Add(new DriveItem(
                        drive.RootDirectory.FullName,
                        $"{label} · {drive.Name} · {FormatBytes(drive.AvailableFreeSpace)} frei"));
                }
                catch
                {
                    // A removable drive can disappear while Windows enumerates it.
                }
            }
        }
        catch
        {
            // The manual folder picker remains available.
        }

        _usb.ItemsSource = items;
        _usb.SelectedIndex = items.Count > 0 ? 0 : -1;
        if (items.Count == 0)
            _status.Text = UiLanguage.T(
                "Kein Wechselmedium automatisch erkannt. USB einstecken und aktualisieren oder ANDEREN USB-/ORDNER WÄHLEN benutzen.");
    }

    private async Task CopyToAsync(string root, string destinationType)
    {
        try
        {
            _status.Text = $"{destinationType}: " + UiLanguage.T("DSFinV-K wird kopiert …");
            var final = Path.Combine(root, Path.GetFileName(_exportFolder));
            var working = final + ".unvollstaendig";

            await Task.Run(() =>
            {
                Directory.CreateDirectory(root);
                if (Directory.Exists(final))
                    throw new InvalidOperationException(
                        "Auf dem Ziel existiert bereits ein gleichnamiger DSFinV-K-Ordner: " + final);
                if (Directory.Exists(working))
                    Directory.Delete(working, recursive: true);

                Directory.CreateDirectory(working);
                foreach (var source in Directory.EnumerateFiles(
                             _exportFolder,
                             "*",
                             SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(_exportFolder, source);
                    var target = Path.Combine(working, relative);
                    var targetDir = Path.GetDirectoryName(target);
                    if (!string.IsNullOrWhiteSpace(targetDir))
                        Directory.CreateDirectory(targetDir);
                    File.Copy(source, target, overwrite: false);
                }

                Directory.Move(working, final);
            });

            await _audit.WriteAsync(
                _actor,
                "DSFINVK_EXPORT_COPY",
                "DSFINV_K",
                $"{_from:yyyy-MM-dd}/{_to:yyyy-MM-dd}",
                $"{destinationType}; Ziel={final}");

            _status.Text = "✓ " + UiLanguage.T("DSFinV-K vollständig kopiert") + $": {final}";
        }
        catch (Exception ex)
        {
            _status.Text = $"⚠ {destinationType}-" + UiLanguage.T("Kopie fehlgeschlagen") + $": {ex.Message}";
        }
    }

    private async Task SendEmailAsync()
    {
        var recipient = (_recipient.Text ?? "").Trim();
        try
        {
            _ = new System.Net.Mail.MailAddress(recipient);
        }
        catch
        {
            _status.Text = UiLanguage.T("⚠ Bitte eine gültige Empfänger-E-Mail eingeben.");
            return;
        }

        var tempRoot = Path.Combine(
            Path.GetTempPath(),
            "TOR-POS",
            "DSFinV-K-Mail");
        Directory.CreateDirectory(tempRoot);
        var zip = Path.Combine(
            tempRoot,
            Path.GetFileName(_exportFolder) + "-" + Guid.NewGuid().ToString("N") + ".zip");

        GoogleGmailService? gmail = null;
        try
        {
            _status.Text = UiLanguage.T("DSFinV-K ZIP-Paket wird erstellt …");
            // Verified byte for byte against the export folder; a package that
            // differs is refused instead of being sent.
            var package = await Task.Run(() => DsfinvkPackage.Create(_exportFolder, zip));

            var size = package.Bytes;
            if (size > MaxMailZipBytes)
            {
                _status.Text =
                    "⚠ " + UiLanguage.T("ZIP-Paket ist") + $" {FormatBytes(size)} " +
                    UiLanguage.T("groß. E-Mail-Versand ist auf 15 MB begrenzt; bitte USB/Datenträger verwenden.");
                return;
            }

            var database = new SqliteDatabase(AppPaths.DatabasePath);
            var management = new BusinessManagementService(
                database,
                _settings,
                _audit);

            if (App.CloudSync is { } cloud)
                gmail = new GoogleGmailService(_settings, cloud);

            var email = new ReportEmailService(
                _settings,
                management,
                google: gmail,
                cloud: App.CloudSync);

            var subject =
                $"TOR POS · DSFinV-K 2.4 · {_from:dd.MM.yyyy}–{_to:dd.MM.yyyy}";
            var body =
                "Anbei der vollständige TOR POS DSFinV-K 2.4 Export als ZIP-Paket.\r\n" +
                $"Zeitraum: {_from:dd.MM.yyyy} bis {_to:dd.MM.yyyy}\r\n\r\n" +
                "Das ZIP enthält den vollständigen Exportordner einschließlich CSV-Dateien, index.xml, GDPdU-DTD und TOR-Exportprotokoll.\r\n" +
                $"SHA-256 des ZIP-Pakets: {package.Sha256}\r\n" +
                "Die Dateien sind unverändert; jede Datei wurde vor dem Versand mit dem Export verglichen.";

            _status.Text = UiLanguage.T("E-Mail wird an") + $" {recipient} " + UiLanguage.T("gesendet …");
            await email.SendFilesAsync(
                recipient,
                subject,
                body,
                new[] { zip });

            await _audit.WriteAsync(
                _actor,
                "DSFINVK_EXPORT_EMAIL",
                "DSFINV_K",
                $"{_from:yyyy-MM-dd}/{_to:yyyy-MM-dd}",
                $"Empfänger={recipient}; ZIP={Path.GetFileName(zip)}; Bytes={size}; Dateien={package.FileCount}; SHA-256={package.Sha256}");

            _status.Text =
                "✓ " + UiLanguage.T("DSFinV-K wurde per E-Mail an") + $" {recipient} " + UiLanguage.T("gesendet.");
        }
        catch (Exception ex)
        {
            _status.Text =
                UiLanguage.T("⚠ DSFinV-K E-Mail-Versand fehlgeschlagen") + ": " + ex.Message +
                "\n" + UiLanguage.T("Der lokale Export bleibt unverändert erhalten; USB-Kopie ist weiterhin möglich.");
        }
        finally
        {
            gmail?.Dispose();
            try
            {
                if (File.Exists(zip))
                    File.Delete(zip);
                if (File.Exists(zip + ".sha256"))
                    File.Delete(zip + ".sha256");
            }
            catch
            {
                // Temporary delivery package is not the canonical export.
            }
        }
    }

    private static StackPanel Section(string title, params Control[] controls)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeight.Bold
        });
        foreach (var control in controls)
            panel.Children.Add(control);
        return panel;
    }

    private static Button Button(string text) => new()
    {
        Content = text,
        MinHeight = 46,
        FontWeight = FontWeight.SemiBold
    };

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024L * 1024L)
            return $"{bytes / (1024d * 1024d * 1024d):0.0} GB";
        if (bytes >= 1024L * 1024L)
            return $"{bytes / (1024d * 1024d):0.0} MB";
        return $"{bytes / 1024d:0.0} KB";
    }

    private sealed record DriveItem(string Root, string Label)
    {
        public override string ToString() => Label;
    }
}
