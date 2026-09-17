using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public partial class SettingsWindow : Window
{
    private readonly ISettingsRepository _settings;
    private readonly DatabaseBackupService _backup;
    private readonly PerformanceCounters _performance;
    private readonly ITseProvider _tseProvider;
    private readonly IReceiptPrinterService _receiptPrinter;
    private readonly IPaymentTerminalService _paymentTerminal;
    private readonly IFiscalComplianceService _compliance;
    private readonly IDsfinvkExportService _dsfinvkExport;
    private readonly IAuditLog _audit;
    private readonly ICommercialLicenseService _commercialLicense;
    private readonly IAuthenticationService _authentication;
    private readonly AuthenticatedUser _currentUser;
    private readonly IAppWindowFactory _windowFactory;
    private readonly string _initialPage;
    private bool _technicianUnlocked;
    private int _technicianFailedAttempts;
    private DateTimeOffset _technicianLockedUntil = DateTimeOffset.MinValue;
    private int _lastNavigationIndex;

    private readonly TextBlock _legalMode = new();
    private readonly TextBlock _legalEas = new();
    private readonly TextBlock _legalTse = new();
    private readonly TextBlock _legalDsfinvk = new();
    private readonly TextBlock _legalReceipt = new();
    private readonly TextBlock _legalParken = new();
    private readonly TextBlock _legalPfand = new();
    private readonly TextBlock _licenseState = new();
    private readonly TextBlock _licenseCustomerNumber = new();
    private readonly TextBlock _licenseCustomer = new();
    private readonly TextBlock _licenseValidity = new();
    private readonly TextBlock _touchLayoutSummary = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.AccentBlue,
        FontWeight = FontWeight.SemiBold
    };

    private readonly Dictionary<string,TextBox> _text = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,CheckBox> _check = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,ComboBox> _combo = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string,Control> _pages = new(StringComparer.OrdinalIgnoreCase);
    private readonly TextBox _reportSmtpPassword = new()
    {
        MinHeight = 38,
        PasswordChar = '●',
        PlaceholderText = "Google App-Passwort eingeben"
    };
    private string _reportSmtpPasswordProtected = "";
    private readonly TextBlock _googleMailStatus = new()
    {
        Text = "Google-Verbindung wird geladen …",
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeight.SemiBold
    };

    // v0.7.33: customer-facing settings are deliberately grouped into a few
    // understandable areas. Technical/fiscal details remain available, but
    // are kept out of the normal day-to-day configuration path.
    private readonly string[] _navigation =
    [
        "Kasse & Bedienung",
        "Firma & Bon",
        "Artikel & Steuern",
        "Zahlung",
        "Geräte",
        "Personal",
        "Berichte & E-Mail",
        "Datensicherung",
        "Software & Update",
        "Erweitert / Techniker 🔒"
    ];

    public SettingsWindow(
        ISettingsRepository settings,
        DatabaseBackupService backup,
        PerformanceCounters performance,
        ITseProvider tseProvider,
        IReceiptPrinterService receiptPrinter,
        IPaymentTerminalService paymentTerminal,
        IFiscalComplianceService compliance,
        IDsfinvkExportService dsfinvkExport,
        IAuditLog audit,
        ICommercialLicenseService commercialLicense,
        IAuthenticationService authentication,
        AuthenticatedUser currentUser,
        IAppWindowFactory windowFactory,
        string initialPage = "Allgemein")
    {
        InitializeComponent();
        _settings = settings;
        _backup = backup;
        _performance = performance;
        _tseProvider = tseProvider;
        _receiptPrinter = receiptPrinter;
        _paymentTerminal = paymentTerminal;
        _compliance = compliance;
        _dsfinvkExport = dsfinvkExport;
        _audit = audit;
        _commercialLicense = commercialLicense;
        _authentication = authentication;
        _currentUser = currentUser;
        _windowFactory = windowFactory;
        _initialPage = initialPage;

        NavList.ItemsSource = _navigation;
        BuildPages();

        Opened += async (_,_) =>
        {
            await LoadAsync();
            await LoadTechnicianGuardAsync();
            await RefreshLegalStatusAsync();
            var requestedPage = ResolveNavigationPage(_initialPage);
            var index = Array.FindIndex(_navigation, x =>
                string.Equals(x, requestedPage, StringComparison.OrdinalIgnoreCase));
            NavList.SelectedIndex = index >= 0 ? index : 0;
            UiLanguage.Apply(this);
        };
    }

    private void BuildPages()
    {
        _pages["Kasse & Bedienung"] = GroupPage(
            "Kasse & Bedienung",
            "Nur die Einstellungen, die der Betreiber im Alltag wirklich benötigt.",
            GeneralPage(), FunctionsPage());

        _pages["Firma & Bon"] = GroupPage(
            "Firma & Bon",
            "Geschäftsdaten und sichtbare Bon-Einstellungen an einer Stelle.",
            CompanyPage(), ReceiptPage());

        _pages["Artikel & Steuern"] = GroupPage(
            "Artikel & Steuern",
            "Nur die steuerlichen Grundlagen. Artikelpflege bleibt unter STAMMDATEN.",
            TaxesPage());

        _pages["Zahlung"] = GroupPage(
            "Zahlung",
            "Welche Zahlarten der Kassierer verwenden darf.",
            PaymentsPage());

        _pages["Geräte"] = GroupPage(
            "Geräte",
            "Geräte ein- oder ausschalten und mit einem einfachen Test prüfen.",
            DevicesPage(), TorCloudPage(), ScannerPage());

        _pages["Personal"] = GroupPage(
            "Personal",
            "Benutzer und Berechtigungen.",
            PermissionsPage());

        _pages["Berichte & E-Mail"] = ReportsEmailPage();

        _pages["Datensicherung"] = BackupPage();

        _pages["Software & Update"] = SoftwareUpdatePage();

        _pages["Erweitert / Techniker 🔒"] = GroupPage(
            "Erweitert / Techniker",
            "Passwortgeschützter Servicebereich. Netzwerk, Ports, Treiber, TSE, Fiskal und Systemdiagnose gehören hierher.",
            AdvancedCashPage(), PaymentTerminalPage(), TechnicalHardwarePage(), TechnicalAccountingPage(),
            TsePage(), LegalPage(), LicensePage(), SystemPage());
    }

    private static string ResolveNavigationPage(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) return "Kasse & Bedienung";
        return requested.Trim() switch
        {
            "Allgemein" or "Funktionen" => "Kasse & Bedienung",
            "Firmendaten" or "Bon & Rechnung" => "Firma & Bon",
            "Steuern" => "Artikel & Steuern",
            "Zahlarten" => "Zahlung",
            "Kartenzahlung / Terminal" => "Erweitert / Techniker 🔒",
            "Geräte-Manager" or "Scanner" => "Geräte",
            "Personal & Rechte" => "Personal",
            "Berichte" or "E-Mail" or "Berichte & E-Mail" => "Berichte & E-Mail",
            "Update" or "Software" or "Software & Update" => "Software & Update",
            "TSE-Aktivierung" or "Recht & Fiskal" or "Lizenzierung" or "System" or "Erweitert / Techniker" => "Erweitert / Techniker 🔒",
            _ => requested
        };
    }

    private Control GroupPage(string title, string subtitle, params Control[] sections)
    {
        var panel = new StackPanel { Spacing = 22 };
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#14263A")),
            BorderBrush = new SolidColorBrush(Color.Parse("#31526C")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16),
            Child = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = title, FontSize = 29, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = subtitle, Opacity = 0.72, TextWrapping = TextWrapping.Wrap }
                }
            }
        });

        foreach (var section in sections)
        {
            panel.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#101925")),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(16),
                Child = section
            });
        }
        return panel;
    }

    private Control GeneralPage()
    {
        var page = Page("Alltag",
            "Die Kasse soll nach der Einrichtung ohne technische Entscheidungen bedienbar sein.");

        var section = Section("Start & Anzeige");
        section.Children.Add(ToggleRow(Check("ui.keyboard.auto", "Bildschirmtastatur bei Berührung öffnen")));
        Form(section, "Kassenname", Text("cash.register.name"),
            "Zum Beispiel Kasse 1, Theke oder Eingang.");
        section.Children.Add(ReadOnlyRow(
            "Betriebsart",
            $"{InstallationEdition.ReadLocked() ?? "NICHT FESTGELEGT"} · bei Installation festgelegt"));
        Form(section, "Startansicht", Combo("startup.view", "KASSE", "OFFICE"),
            "Für den normalen Betrieb wird KASSE empfohlen.");
        Form(section, "Darstellung", Combo("ui.scale", "AUTO", "KOMPAKT", "STANDARD", "GROSS"),
            "AUTO ist für die meisten Touchscreens die beste Wahl.");
        page.Children.Add(section);

        page.Children.Add(InfoCard(
            "Einfach gehalten",
            "Rastergrößen, Schrift-Feinabstimmung, Kassennummer und technische Anzeigeparameter liegen im geschützten Technikerbereich.",
            AppTheme.InfoCardBg));
        return page;
    }

    private Control CompanyPage()
    {
        var page = Page("Firmendaten",
            "Geschäftsdaten werden zentral gepflegt und später für Bon, Berichte und fiskale Dokumente verwendet.");

        var section = Section("Geschäftsinformationen");
        Form(section, "Firma", Text("company.name"));
        Form(section, "Inhaber / Betreiber", Text("company.owner"));
        Form(section, "Straße", Text("company.street"));
        Form(section, "PLZ", Text("company.zip"));
        Form(section, "Ort", Text("company.city"));
        Form(section, "Telefon", Text("company.phone"));
        Form(section, "E-Mail", Text("company.email"));
        Form(section, "Steuernummer", Text("company.tax_no"));
        Form(section, "USt-IdNr.", Text("company.vat_id"));
        page.Children.Add(section);

        var use = Section("Verwendung");
        use.Children.Add(ToggleRow(Check("company.show_on_receipt", "Firmendaten auf dem Bon verwenden")));
        use.Children.Add(ToggleRow(Check("company.show_on_reports", "Firmendaten auf Berichten verwenden")));
        page.Children.Add(use);

        return page;
    }

    private Control FunctionsPage()
    {
        var page = Page("Bedienfunktionen",
            "Nur Funktionen einschalten, die der Betrieb tatsächlich benutzt.");

        var section = Section("Verkauf");
        section.Children.Add(ToggleRow(Check("function.free_price", "Sonstige Artikel / freie Preiseingabe anzeigen")));
        if (InstallationEdition.ReadLocked() == "KIOSK")
            section.Children.Add(ToggleRow(Check("function.pfand_buttons", "Pfand / Leergut anzeigen")));
        else
            section.Children.Add(ReadOnlyRow("IMBISS Extras", "Verwaltung direkt im Artikel / unter STAMMDATEN → EXTRAS"));
        section.Children.Add(ToggleRow(Check("function.low_stock", "Bei kritischem Warenbestand warnen")));
        Form(section, "Mindestbestand", Text("function.low_stock_threshold"), "Warnschwelle, z.B. 5 Stück.");
        page.Children.Add(section);

        if (InstallationEdition.ReadLocked() == "IMBISS")
        {
            var pickup = Section("Abholnummer / Bestellablauf");
            pickup.Children.Add(ReadOnlyRow("Schnellauswahl", "OFF = aus · SALE = Nummer beim Kassieren · ORDER = Nummer sofort bei Bestellannahme"));
            Form(pickup, "Abholnummer / Bestellablauf", Combo("imbiss.pickup_number.mode", "OFF", "SALE", "ORDER"),
                "Empfohlen für Döner/Imbiss: ORDER. Dann erscheint in der Kasse BESTELLUNG ANNEHMEN · F3 · ABHOLNR.; der eigentliche Bon entsteht erst später bei BAR/KARTE.");
            pickup.Children.Add(ToggleRow(Check("imbiss.order.number_enabled", "Bei Bestellannahme eine Abholnummer vergeben")));
            pickup.Children.Add(ToggleRow(Check("imbiss.pickup_slip.auto_print", "Bei ORDER einen Abholschein für den Kunden drucken")));
            pickup.Children.Add(ReadOnlyRow("Training", "ORDER kann jetzt auch mit dem TRAINING-Benutzer getestet werden. Trainingsbestellungen und Trainings-Abholnummern bleiben getrennt von echten offenen Bestellungen."));
            pickup.Children.Add(ReadOnlyRow("Wichtig", "Abholnummer ist nur eine Betriebs-/Wartenummer und ersetzt niemals die Bonnummer."));
            page.Children.Add(pickup);
        }

        var close = Section("Tagesabschluss");
        close.Children.Add(ToggleRow(Check("function.z_auto_print", "Z-Bericht automatisch drucken")));
        close.Children.Add(ReadOnlyRow("Sicherheitsregel", "Z-Abschluss bleibt bei offenen geparkten Bons gesperrt."));
        page.Children.Add(close);

        return page;
    }

    private Control PaymentsPage()
    {
        var page = Page("Zahlarten",
            "Zahlarten werden separat konfiguriert. Die Karten-Zahlart wird später direkt mit ZVT verbunden.");

        var section = Section("Aktive Zahlarten");
        section.Children.Add(ToggleRow(Check("pay.cash.enabled", "Bar aktiv")));
        section.Children.Add(ToggleRow(Check("pay.card.enabled", "Karte aktiv")));
        section.Children.Add(ToggleRow(Check("pay.invoice.enabled", "Auf Rechnung aktiv")));
        Form(section, "Bezeichnung Bar", Text("pay.cash.label"));
        Form(section, "Bezeichnung Karte", Text("pay.card.label"));
        Form(section, "Bezeichnung Rechnung", Text("pay.invoice.label"));
        page.Children.Add(section);

        var quick = Section("Schnellkassieren · F5");
        Form(
            quick,
            "Standard-Zahlart",
            Combo("pay.quick.default", "AUS", "BAR", "KARTE"),
            "AUS = F5 öffnet die Zahlart-Auswahl. BAR/KARTE = F5 startet die gewählte Zahlart direkt.");
        quick.Children.Add(ToggleRow(Check(
            "pay.quick.cash_exact",
            "Bei F5 + BAR sofort als PASSEND kassieren (kein Rückgeld-Dialog)")));
        quick.Children.Add(ReadOnlyRow(
            "Sicherheitsregel",
            "F1 BAR bleibt immer die normale Barzahlung mit Gegeben/Rückgeld. F2 KARTE bleibt direkte Kartenzahlung. Die gespeicherte Zahlart ist auch beim Schnellkassieren immer eindeutig BAR oder KARTE."));
        page.Children.Add(quick);

        var promotions = Section("Angebote / Rabatte");
        promotions.Children.Add(ToggleRow(Check(
            "promotion.allow_manual_discount",
            "Manuellen Rabatt zusätzlich zu einem aktiven Angebot erlauben")));
        promotions.Children.Add(ReadOnlyRow(
            "Standard",
            "AUS empfohlen: Angebot und manueller Rabatt werden nicht automatisch gestapelt. Pfand wird niemals durch Angebot rabattiert."));
        page.Children.Add(promotions);

        page.Children.Add(InfoCard(
            "Kartenzahlung",
            "Bei aktivierter Terminalintegration wird ein Kartenverkauf erst nach erfolgreicher Terminalbestätigung als Verkauf gespeichert.",
            AppTheme.InfoCardBg));

        return page;
    }

    private Control AdvancedCashPage()
    {
        var page = Page("Kasse · Technische Feinabstimmung",
            "Diese Werte werden bei Installation gesetzt und gehören nicht in den täglichen Betrieb.");
        var baseConfig = Section("Kassenidentität & Darstellung");
        Form(baseConfig, "Kassennummer", Text("cash.register.number"), "Eindeutige Nummer des Kassensystems. Standard: 1.");
        Form(baseConfig, "Theme", Combo("ui.theme", "DUNKEL", "HELL", "SYSTEM"));
        page.Children.Add(baseConfig);

        var layout = Section("Touch-Raster");
        var productColumns = Text("ui.product.columns");
        var productRows = Text("ui.product.rows");
        var categoryColumns = Text("ui.category.columns");
        var categoryRows = Text("ui.category.rows");
        var touchFont = Text("ui.touch.font_size");
        foreach (var box in new[] { productColumns, productRows, categoryColumns, categoryRows, touchFont }) { box.Width = 110; box.TextChanged += (_, _) => UpdateTouchLayoutSummary(); }
        Form(layout, "Artikeltasten · Spalten", productColumns, "2–8 · IMBISS Standard 4 · KIOSK 5");
        Form(layout, "Artikeltasten · Zeilen", productRows, "2–12 · IMBISS Standard 3 · KIOSK 8");
        Form(layout, "Warengruppen · Spalten", categoryColumns, "2–8 · Standard 4");
        Form(layout, "Warengruppen · Zeilen", categoryRows, "1–10 · Standard 6");
        Form(layout, "Schriftgröße · Tasten", touchFont, "11–28 · Standard 18");
        layout.Children.Add(ToggleRow(Check("ui.product.show_images", "Artikelbilder anzeigen")));
        layout.Children.Add(new Border { Background = new SolidColorBrush(Color.Parse("#14263A")), BorderBrush = new SolidColorBrush(Color.Parse("#31526C")), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8), Padding = new Thickness(12), Margin = new Thickness(0,8,0,0), Child = _touchLayoutSummary });
        page.Children.Add(layout);

        var misc = Section("Weitere Kassenlogik");
        Form(misc, "Währung", Text("currency.name"));
        Form(misc, "Abkürzung", Text("currency.symbol"));
        // R139: the cash in the drawer before the first Kassensturz; afterwards
        // every confirmed Kassensturz is the starting point.
        Form(misc, "Anfangsbestand in Cent (bis zum ersten Kassensturz)", Text("cash.start.cents"));
        misc.Children.Add(ToggleRow(Check("function.operator_on_receipt", "Bediener auf Bon anzeigen")));
        misc.Children.Add(ToggleRow(Check("function.options_on_receipt", "Varianten / Optionen auf Bon anzeigen")));
        misc.Children.Add(ToggleRow(Check("function.drawer_on_receipt", "Kassenlade beim Bondruck öffnen")));
        misc.Children.Add(ToggleRow(Check("function.payment_query", "Zahlart vor Abschluss zusätzlich bestätigen")));
        var reasons = Text("function.storno_reasons", true); reasons.MinHeight = 100;
        Form(misc, "Stornogründe", reasons, "Pflichtgrund bei SOFORT STORNO (vor der Zahlung) · mit Zeilenumbruch eingeben.");

        var bonStornoReasons = Text("function.bon_storno_reasons", true); bonStornoReasons.MinHeight = 100;
        Form(misc, "Bon-Storno-Gründe", bonStornoReasons, "Pflichtgrund bei BON STORNO (Gegenbuchung eines abgeschlossenen Bons) · mit Zeilenumbruch eingeben.");

        var discountReasons = Text("function.discount_reasons", true); discountReasons.MinHeight = 100;
        Form(misc, "Rabattgründe", discountReasons, "Pflichtgrund bei RABATT · mit Zeilenumbruch eingeben.");

        var cancelReasons = Text("function.cancel_reasons", true); cancelReasons.MinHeight = 100;
        Form(misc, "Abbruchgründe", cancelReasons, "Pflichtgrund bei C / Verkauf abbrechen · mit Zeilenumbruch eingeben.");

        page.Children.Add(misc);
        return page;
    }

    private Control TechnicalHardwarePage()
    {
        var page = Page("Geräte · Technische Einrichtung",
            "Treiber, Ports und Protokolle werden einmalig vom Techniker eingerichtet.");
        var printer = Section("Bondrucker / Windows");
        printer.Children.Add(ReadOnlyRow("Windows-Drucker", "Auswahl unter Geräte → Drucker / Yazıcılar."));
        var lastTest = Text("device.receipt_printer.last_test"); lastTest.IsReadOnly = true; Form(printer, "Letzter Test", lastTest);
        var lastError = Text("device.receipt_printer.last_error"); lastError.IsReadOnly = true; Form(printer, "Letzter Fehler", lastError);
        printer.Children.Add(ToggleRow(Check("device.receipt_printer.autocut_driver", "Auto-Cut im Windows-Treiber verwenden")));
        
        page.Children.Add(printer);

        var other = Section("Anschlüsse");
        other.Children.Add(ToggleRow(Check("device.drawer.via_printer", "Kassenlade über Drucker/DK-Anschluss steuern")));
        page.Children.Add(other);

        var scanner = Section("Barcode-Scanner · Protokoll");
        Form(scanner, "Scanner-Modus", Combo("scanner.mode", "HID", "COM"), "HID = Scanner verhält sich wie eine Tastatur.");
        scanner.Children.Add(ToggleRow(Check("scanner.enter_suffix", "ENTER-Suffix verwenden (empfohlen)")));
        Form(scanner, "Wartezeit ohne ENTER (ms)", Text("scanner.wait_ms"));
        page.Children.Add(scanner);
        return page;
    }

    private Control TechnicalAccountingPage()
    {
        var page = Page("Buchhaltung · Technische Zuordnung",
            "DATEV-Felder sind Vorbereitung und gehören nicht in die normale Bedienoberfläche.");
        var datev = Section("DATEV – Vorbereitung");
        Form(datev, "19% Konto", Text("tax.datev.standard.account"));
        Form(datev, "19% Gegenkonto", Text("tax.datev.standard.counter"));
        Form(datev, "19% Kennzeichen", Text("tax.datev.standard.code"));
        Form(datev, "7% Konto", Text("tax.datev.reduced.account"));
        Form(datev, "7% Gegenkonto", Text("tax.datev.reduced.counter"));
        Form(datev, "7% Kennzeichen", Text("tax.datev.reduced.code"));
        page.Children.Add(datev);
        return page;
    }

    private Control PaymentTerminalPage()
    {
        var page = Page(
            "Kartenzahlung / Terminal",
            "TOR nutzt in Deutschland primär die herstellerunabhängige ZVT-Kassenschnittstelle. In v0.6.1 ist ZVT über TCP/IP aktiv implementiert.");

        var config = Section("ZVT-Terminal");
        config.Children.Add(ToggleRow(Check(
            "payment.terminal.enabled",
            "Kartenterminal mit TOR POS verbinden")));

        config.Children.Add(ReadOnlyRow(
            "Aktives Protokoll",
            "ZVT · TCP/IP"));

        Form(
            config,
            "Terminal-Profil",
            Combo(
                "payment.terminal.vendor",
                "AUTO_ZVT",
                "INGENICO_ZVT",
                "CCV_ZVT",
                "VERIFONE_ZVT",
                "PAX_PROVIDER_ZVT",
                "OTHER_ZVT"),
            "AUTO_ZVT ist für ein normales ZVT-fähiges Terminal die empfohlene Einstellung.");

        Form(
            config,
            "Modell / Notiz",
            Text("payment.terminal.model"),
            "Zum Beispiel Ingenico DX8000, CCV Pad Next, Verifone P630.");

        Form(
            config,
            "IP-Adresse",
            Text("payment.terminal.ip"),
            "Terminal und TOR-Kasse müssen im selben erreichbaren Netzwerk sein.");

        Form(
            config,
            "TCP-Port",
            Text("payment.terminal.port"),
            "ZVT verwendet häufig Port 20007 oder 20008; maßgeblich ist die Terminalkonfiguration.");

        Form(
            config,
            "Verbindungs-Timeout s",
            Text("payment.terminal.connect_timeout_seconds"));

        Form(
            config,
            "Zahlungs-Timeout s",
            Text("payment.terminal.command_timeout_seconds"));

        config.Children.Add(ToggleRow(Check(
            "payment.terminal.register_before_payment",
            "Vor Zahlung ZVT-Anmeldung ausführen")));

        config.Children.Add(ReadOnlyRow(
            "Sicherheitsregel",
            "KARTE wird erst nach erfolgreicher Terminalbestätigung als Verkauf gespeichert."));

        page.Children.Add(config);

        var actions = Section("Verbindung & Status");

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var probe = new Button
        {
            Content = "VERBINDUNG TESTEN",
            MinWidth = 190,
            MinHeight = 46
        };

        var register = new Button
        {
            Content = "ZVT ANMELDUNG TESTEN",
            MinWidth = 210,
            MinHeight = 46
        };

        var endOfDay = new Button
        {
            Content = "TERMINAL-TAGESABSCHLUSS",
            MinWidth = 230,
            MinHeight = 46
        };

        probe.Click += async (_,_) =>
        {
            await SaveTerminalFieldsAsync();
            StatusText.Text = "Kartenterminal wird gesucht ...";

            var result = await _paymentTerminal.ProbeAsync();

            _text["payment.terminal.last_test"].Text =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _text["payment.terminal.last_status"].Text = result.State;
            _text["payment.terminal.last_error"].Text =
                result.Success ? "" : result.Message;

            StatusText.Text = result.Message;
        };

        register.Click += async (_,_) =>
        {
            await SaveTerminalFieldsAsync();
            StatusText.Text = "ZVT-Anmeldung wird geprüft ...";

            var result = await _paymentTerminal.RegisterAsync();

            _text["payment.terminal.last_test"].Text =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _text["payment.terminal.last_status"].Text = result.State;
            _text["payment.terminal.last_error"].Text =
                result.Success ? "" : result.Message;

            StatusText.Text = result.Message;
        };

        endOfDay.Click += async (_,_) =>
        {
            await SaveTerminalFieldsAsync();
            StatusText.Text = "Terminal-Tagesabschluss wird angestoßen ...";

            var result = await _paymentTerminal.EndOfDayAsync();

            _text["payment.terminal.end_of_day.last_at"].Text =
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            _text["payment.terminal.end_of_day.last_status"].Text = result.State;
            _text["payment.terminal.end_of_day.last_error"].Text =
                result.Success ? "" : result.Message;

            StatusText.Text = result.Message;
        };

        row.Children.Add(probe);
        row.Children.Add(register);
        row.Children.Add(endOfDay);
        actions.Children.Add(row);

        var lastTest = Text("payment.terminal.last_test");
        lastTest.IsReadOnly = true;
        Form(actions, "Letzter Test", lastTest);

        var lastStatus = Text("payment.terminal.last_status");
        lastStatus.IsReadOnly = true;
        Form(actions, "Status", lastStatus);

        var lastError = Text("payment.terminal.last_error");
        lastError.IsReadOnly = true;
        Form(actions, "Letzter Fehler", lastError);

        page.Children.Add(actions);

        var endOfDaySection = Section("Terminal-Tagesabschluss (ZVT End-of-Day)");
        endOfDaySection.Children.Add(ReadOnlyRow(
            "Was passiert hier",
            "Fordert das Terminal auf, seinen eigenen gespeicherten Tagesumsatz an den Netzbetreiber/Acquirer zu übertragen und dort abzuschließen. " +
            "Das ist unabhängig von TOR POS' eigenem Z-Bericht/Kassenabschluss - es gleicht ab, was der Netzbetreiber tatsächlich abrechnet, nicht TOR's eigene Fiskaldaten."));

        var eodLastAt = Text("payment.terminal.end_of_day.last_at");
        eodLastAt.IsReadOnly = true;
        Form(endOfDaySection, "Letzter Tagesabschluss", eodLastAt);

        var eodLastStatus = Text("payment.terminal.end_of_day.last_status");
        eodLastStatus.IsReadOnly = true;
        Form(endOfDaySection, "Status", eodLastStatus);

        var eodLastError = Text("payment.terminal.end_of_day.last_error");
        eodLastError.IsReadOnly = true;
        Form(endOfDaySection, "Letzter Fehler", eodLastError);

        page.Children.Add(endOfDaySection);

        var compatibility = Section("Kompatibilität am deutschen Markt");

        foreach (var profile in _paymentTerminal.Profiles)
        {
            var statusText = new TextBlock
            {
                Text =
                    $"{profile.Manufacturer} · {profile.Family}\n" +
                    $"{profile.Integration} · {profile.TorStatus}\n" +
                    profile.Notes,
                TextWrapping = TextWrapping.Wrap
            };

            compatibility.Children.Add(ToggleRow(statusText));
        }

        page.Children.Add(compatibility);

        page.Children.Add(InfoCard(
            "PCI / Kartendaten",
            "TOR POS speichert keine vollständige Kartennummer, keine Track-Daten, keine PIN und keinen CVV/CVC. Für die Kassenabstimmung werden nur sichere Terminalreferenzen wie Terminal-ID, Terminalbelegnummer, Trace-Nummer und Kartenname verwendet.",
            AppTheme.InfoCardBg));

        page.Children.Add(InfoCard(
            "Nicht alles ist ZVT",
            "SumUp, Stripe Terminal und Adyen verwenden eigene Integrationswege. TOR zeigt sie deshalb getrennt und behauptet keine universelle ZVT-Kompatibilität, wenn sie nicht verifiziert ist.",
            AppTheme.WarningAmberBg));

        return page;
    }

    private async Task SaveTerminalFieldsAsync()
    {
        var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in new[]
        {
            "payment.terminal.model",
            "payment.terminal.ip",
            "payment.terminal.port",
            "payment.terminal.connect_timeout_seconds",
            "payment.terminal.command_timeout_seconds"
        })
        {
            if (_text.TryGetValue(key, out var box))
                values[key] = box.Text ?? "";
        }

        if (_check.TryGetValue("payment.terminal.enabled", out var enabled))
            values["payment.terminal.enabled"] =
                (enabled.IsChecked == true).ToString().ToLowerInvariant();

        if (_check.TryGetValue(
            "payment.terminal.register_before_payment",
            out var registration))
        {
            values["payment.terminal.register_before_payment"] =
                (registration.IsChecked == true).ToString().ToLowerInvariant();
        }

        if (_combo.TryGetValue("payment.terminal.vendor", out var vendor))
            values["payment.terminal.vendor"] =
                vendor.SelectedItem?.ToString() ?? "AUTO_ZVT";

        values["payment.terminal.protocol"] = "ZVT_TCP";

        await _settings.SaveManyAsync(values);
            TouchKeyboard.AutoOpen = values.GetValueOrDefault("ui.keyboard.auto", "true") != "false";
    }

    private Control TaxesPage()
    {
        var page = Page("Steuern",
            "Nur die im Betrieb benötigten Mehrwertsteuersätze werden hier gezeigt.");

        var rates = Section("Mehrwertsteuer");
        Form(rates, "Standard-MwSt. %", Text("tax.standard"));
        Form(rates, "Ermäßigte MwSt. %", Text("tax.reduced"));
        Form(rates, "Standard bei neuen Artikeln", Combo("tax.default", "19", "7"));
        page.Children.Add(rates);
        page.Children.Add(InfoCard(
            "Wichtig",
            "Der Steuersatz wird artikelbezogen gepflegt. Technische DATEV-Zuordnungen befinden sich im Technikerbereich.",
            AppTheme.InfoCardBg));
        return page;
    }

    private Control ReceiptPage()
    {
        var page = Page("Bon & Rechnung",
            "Bonkopf, Logo, Fußzeile und Druckverhalten werden zentral gepflegt.");

        var logo = Section("Bonlogo");
        var logoPath = Text("receipt.logo_path");
        logoPath.IsReadOnly = true;
        logoPath.PlaceholderText = "Kein Logo ausgewählt";
        Form(logo, "Logo-Datei", logoPath, "PNG oder JPG auswählen. TOR kopiert das Logo in den eigenen Datenordner; der Original-Dateipfad muss später nicht verfügbar bleiben.");

        var logoStatus = new TextBlock
        {
            Text = "Einmal auswählen, speichern – TOR setzt das Logo danach automatisch oben auf den Bon.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68
        };
        var logoButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };
        var chooseLogo = new Button { Content = "BONLOGO AUSWÄHLEN", MinHeight = 46, MinWidth = 190, FontWeight = FontWeight.Bold };
        var removeLogo = new Button { Content = "LOGO ENTFERNEN", MinHeight = 46, MinWidth = 160 };
        chooseLogo.Click += async (_,_) => await ChooseReceiptLogoAsync(logoPath, logoStatus);
        removeLogo.Click += async (_,_) => await RemoveReceiptLogoAsync(logoPath, logoStatus);
        logoButtons.Children.Add(chooseLogo);
        logoButtons.Children.Add(removeLogo);
        logo.Children.Add(logoButtons);
        logo.Children.Add(logoStatus);
        page.Children.Add(logo);

        var text = Section("Bontexte");
        var header = Text("receipt.header", true); header.MinHeight = 80;
        var footer = Text("receipt.footer", true); footer.MinHeight = 80;
        Form(text, "Kopfzeile", header);
        Form(text, "Fußzeile", footer);
        Form(text, "Ausrichtung", Combo("receipt.alignment", "ZENTRIERT", "LINKS"));
        Form(text, "Zeichenbreite", Combo("receipt.font_width", "32", "42", "48"),
            "Abhängig vom später gewählten Bondruckerprofil.");
        page.Children.Add(text);

        var behavior = Section("Druckverhalten");
        behavior.Children.Add(ToggleRow(Check("receipt.auto_print", "Bon nach Verkauf automatisch drucken")));
        behavior.Children.Add(ToggleRow(Check("receipt.last_receipt_enabled", "Funktion 'Letzter Bon' aktivieren")));
        behavior.Children.Add(ToggleRow(Check("receipt.tse_qr_code.enabled", "TSE-Angaben als QR-Code statt als Text drucken (kürzerer Bon)")));
        behavior.Children.Add(InfoCard("QR-Code statt Bontext",
            "Wenn aktiviert, werden eAS, TSE-Seriennummer, Transaktionsnummer, Signaturzähler und Prüfwert als ein QR-Code gedruckt statt als fünf einzelne Textzeilen - der Bon wird dadurch kürzer. " +
            "Falls die QR-Erzeugung fehlschlägt, druckt TOR POS automatisch die Textzeilen als Rückfalllösung; die Angaben fehlen nie ersatzlos.",
            AppTheme.InfoCardBg));
        behavior.Children.Add(ToggleRow(Check("printer.auto_cut.enabled", "Bon nach dem Druck automatisch abschneiden")));
        behavior.Children.Add(ToggleRow(Check("printer.drawer_kick.enabled", "Kassenschublade bei Barzahlung automatisch öffnen")));
        behavior.Children.Add(InfoCard("Schnitt & Kassenschublade",
            "TOR POS sendet den Schnitt- und Schubladenbefehl direkt als StarPRNT/ESC-POS-Rohbefehl an den Drucker, unabhängig vom Windows-Treiber. " +
            "Die Kassenschublade öffnet nur beim Original-Bon einer Barzahlung, nie bei Kartenzahlung, Testdruck oder Bon-Kopien aus der Bon-Historie. " +
            "Schlägt der Rohbefehl fehl, bleibt der eigentliche Bondruck davon unberührt - nur Schnitt/Schublade unterbleiben dann.",
            AppTheme.InfoCardBg));
        page.Children.Add(behavior);

        // R103: paperless alternative - a QR code shown on screen whenever
        // BON EIN/AUS is off for a sale (so the customer never leaves with
        // nothing at all), pointing at a small web page served directly
        // from this till over the shop's own WiFi. Requires a restart to
        // start/stop the local server after toggling.
        var digital = Section("Digitaler Bon (QR-Code)");
        digital.Children.Add(ToggleRow(Check("receipt.digital_qr.enabled", "Digitalen Bon per QR-Code anbieten, wenn BON EIN/AUS ausgeschaltet ist")));
        Form(digital, "Port", Text("receipt.digital_qr.port"),
            "Lokaler Port für die Bon-Webseite auf dieser Kasse, Standard 8099. Nach Änderung TOR POS neu starten.");
        digital.Children.Add(InfoCard("Wie das funktioniert",
            "Ist BON EIN/AUS für einen Verkauf ausgeschaltet, zeigt die Kasse statt eines Papierbons einen QR-Code auf dem Bildschirm. " +
            "Der Kunde scannt ihn mit dem Handy, während er im WLAN dieses Geschäfts ist, und sieht den Bon als Webseite - kein Papier, keine App, kein Internet außerhalb des Geschäfts nötig. " +
            "§6 KassenSichV erlaubt einen elektronischen statt gedruckten Beleg, solange der Kunde ihn tatsächlich mitnehmen kann.",
            AppTheme.InfoCardBg));
        page.Children.Add(digital);

        return page;
    }

    private async Task ChooseReceiptLogoAsync(TextBox logoPath, TextBlock status)
    {
        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Bonlogo auswählen",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("Bilddateien")
                        {
                            Patterns = ["*.png", "*.jpg", "*.jpeg", "*.bmp"]
                        }
                    ]
                });

            var file = files.FirstOrDefault();
            if (file is null) return;

            var source = file.Path.LocalPath;
            var info = new FileInfo(source);
            if (!info.Exists)
                throw new InvalidOperationException("Die ausgewählte Datei wurde nicht gefunden.");
            if (info.Length > 5 * 1024 * 1024)
                throw new InvalidOperationException("Das Bonlogo darf maximal 5 MB groß sein.");

            var ext = Path.GetExtension(source).ToLowerInvariant();
            if (ext is not ".png" and not ".jpg" and not ".jpeg" and not ".bmp")
                throw new InvalidOperationException("Bitte PNG, JPG oder BMP verwenden.");

            Directory.CreateDirectory(AppPaths.ReceiptAssetsPath);
            var target = Path.Combine(AppPaths.ReceiptAssetsPath, "bon-logo" + ext);
            foreach (var old in Directory.EnumerateFiles(AppPaths.ReceiptAssetsPath, "bon-logo.*"))
            {
                if (!string.Equals(Path.GetFullPath(old), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(old); } catch { }
                }
            }

            if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
                File.Copy(source, target, true);

            logoPath.Text = target;
            await _settings.SaveManyAsync(new Dictionary<string,string>
            {
                ["receipt.logo_path"] = target
            });
            await _audit.WriteAsync(_currentUser.Username, "RECEIPT_LOGO_SET", "SETTINGS", "", Path.GetFileName(target));
            status.Text = "Bonlogo aktiv · wird automatisch oben auf neue Bon-Ausdrucke gesetzt.";
            StatusText.Text = "Bonlogo gespeichert";
        }
        catch (Exception ex)
        {
            status.Text = "Logo konnte nicht übernommen werden: " + ex.Message;
        }
    }

    private async Task RemoveReceiptLogoAsync(TextBox logoPath, TextBlock status)
    {
        try
        {
            logoPath.Text = "";
            await _settings.SaveManyAsync(new Dictionary<string,string>
            {
                ["receipt.logo_path"] = ""
            });
            foreach (var old in Directory.EnumerateFiles(AppPaths.ReceiptAssetsPath, "bon-logo.*"))
            {
                try { File.Delete(old); } catch { }
            }
            await _audit.WriteAsync(_currentUser.Username, "RECEIPT_LOGO_REMOVE", "SETTINGS", "", "Bonlogo entfernt");
            status.Text = "Kein Bonlogo aktiv.";
            StatusText.Text = "Bonlogo entfernt";
        }
        catch (Exception ex)
        {
            status.Text = "Logo konnte nicht entfernt werden: " + ex.Message;
        }
    }

    private Control DevicesPage()
    {
        var page = Page("Drucker / Yazıcılar & Geräte",
            "Windows-Drucker auswählen, testen und unten SPEICHERN drücken. A4-Drucker sind für Testausgaben ebenfalls auswählbar.");
        var sumup = new Button { Content = "SUMUP SOLO · VERBINDUNG / 1,00 € TEST", MinHeight = 48 };
        sumup.Click += async (_, _) =>
        {
            if (!_currentUser.IsAdmin) return;
            await new SumUpConnectionWindow().ShowDialog(this);
        };
        page.Children.Add(sumup);
        page.Children.Add(PrinterSelection("Bondrucker", "device.receipt_printer"));
        if (InstallationEdition.ReadLocked() == "IMBISS")
        {
            page.Children.Add(PrinterSelection("Küchendrucker / 2. Drucker", "device.kitchen_printer"));
            page.Children.Add(ToggleRow(Check("device.kitchen_printer.auto_print", "Bestellung automatisch an Küchendrucker senden")));
            page.Children.Add(InfoCard("Küchenbon", "Küchenbons bleiben immer auf Deutsch und sind klar als KEIN STEUERBELEG gekennzeichnet. Im ORDER-Modus wird die Küche direkt bei Bestellannahme informiert; OFF/SALE drucken erst beim Kassieren.", AppTheme.InfoCardBg));
            page.Children.Add(InfoCard("Küchenrouting nach Station", "Jeder Warengruppe kann in der Artikelverwaltung eine Küchenstation (Grill, Fritteuse, Getränke) zugewiesen werden. " +
                "Hier unten kann für jede Station optional ein eigener Drucker aktiviert werden. Warengruppen ohne Station oder eine hier deaktivierte Station " +
                "drucken weiterhin auf dem Küchendrucker oben.", AppTheme.InfoCardBg));
            page.Children.Add(PrinterSelection("Küchendrucker · Grill", KitchenStations.SettingsPrefix(KitchenStations.Grill)));
            page.Children.Add(PrinterSelection("Küchendrucker · Fritteuse", KitchenStations.SettingsPrefix(KitchenStations.Fritteuse)));
            page.Children.Add(PrinterSelection("Küchendrucker · Getränke", KitchenStations.SettingsPrefix(KitchenStations.Getraenke)));
        }
        page.Children.Add(PrinterSelection("A4-Drucker", "device.a4_printer"));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var refresh = new Button { Content = "GERÄTESTATUS AKTUALISIEREN", MinHeight = 46 };
        refresh.Click += (_, _) =>
        {
            string Value(string key) => _text.TryGetValue(key, out var box) && !string.IsNullOrWhiteSpace(box.Text) ? box.Text! : "Nicht geprüft";
            status.Text = "Gespeicherte / zuletzt geprüfte Angaben – kein Live-Verbindungstest\n" +
                "Kartenterminal: " + Value("payment.terminal.last_status") + " · " + Value("payment.terminal.last_test") +
                "\nTerminalhinweis: " + Value("payment.terminal.last_error") +
                "\nTSE: " + Value("tse.status") + " · " + Value("tse.last_test") +
                "\nTSE-Hinweis: " + Value("tse.last_error") +
                "\nVerbindung prüfen / konfigurieren: Erweitert / Techniker → Zahlung bzw. TSE.\n" +
                "Produktivfreigabe ist separat erforderlich; ein erreichbares Gerät genügt nicht.";
        };
        var devices = Section("Terminal & TSE");
        devices.Children.Add(refresh); devices.Children.Add(status);
        page.Children.Add(devices);
        page.Children.Add(ToggleRow(Check("device.drawer.enabled", "Kassenlade verwenden")));

        // R104: a genuine second screen (e.g. HP L7010t POS-Monitor), same
        // "own fullscreen window on a configured screen" mechanism as the
        // Bestellmonitor below - not a serial/COM-port text display.
        var customerDisplayEnabled = Check("device.customer_display.enabled", "Kundendisplay verwenden");
        page.Children.Add(ToggleRow(customerDisplayEnabled));

        var customerDisplaySection = Section("Kundendisplay · eigener Bildschirm");
        Form(customerDisplaySection, "Bildschirm", Combo("device.customer_display.screen_index", "0", "1", "2", "3", "4"), "0 = automatisch den zweiten Bildschirm verwenden; 1–4 = feste Bildschirmnummer.");
        customerDisplaySection.Children.Add(InfoCard("Getrennt vom Bestellmonitor", "Das Kundendisplay zeigt den Warenkorb/die Summe während des Verkaufs und danach 'Vielen Dank' - bei aktiviertem digitalem Bon (R103) auch den QR-Code dort statt auf dem Kassenbildschirm. Der Bestellmonitor unten ist ein eigener, unabhängiger Bildschirm nur für Bestellnummern und deren Status.", AppTheme.InfoCardBg));
        customerDisplaySection.IsVisible = false;
        customerDisplayEnabled.IsCheckedChanged += (_, _) => customerDisplaySection.IsVisible = customerDisplayEnabled.IsChecked == true;
        page.Children.Add(customerDisplaySection);

        if (InstallationEdition.ReadLocked() == "IMBISS")
        {
            var orderEnabled = Check("order_display.enabled", "Separaten Bestellmonitor verwenden");
            page.Children.Add(ToggleRow(orderEnabled));

            var orderDisplay = Section("Bestellmonitor · eigener Bildschirm");
            Form(orderDisplay, "Bildschirm", Combo("order_display.screen_index", "0", "1", "2", "3", "4"), "0 = automatisch den zweiten Bildschirm verwenden; 1–4 = feste Bildschirmnummer.");
            Form(orderDisplay, "Aktualisierung (Sek.)", Text("order_display.refresh_seconds"), "1–10 Sekunden · Standard 2.");
            orderDisplay.Children.Add(InfoCard("Nur Bestellungen", "ANGENOMMEN / IN VORBEREITUNG erscheinen als WIRD VORBEREITET. ABHOLBEREIT erscheint grün als FERTIG. AUSGEGEBEN verschwindet vom Bestellmonitor.", AppTheme.InfoCardBg));
            orderDisplay.IsVisible = false;
            orderEnabled.IsCheckedChanged += (_, _) => orderDisplay.IsVisible = orderEnabled.IsChecked == true;
            page.Children.Add(orderDisplay);
        }
        return page;
    }

    private Control TorCloudPage()
    {
        var page=Page("TOR POS Cloud · Synchronisierung",
            "Verbindung und Bestand direkt mit dem Kundenportal prüfen. Testbons werden nicht als Umsatz übertragen.");
        // Dedicated save action: generic settings-save must never partially change a Cloud identity.
        var url=new TextBox {MinWidth=300,MinHeight=42,PlaceholderText="http://127.0.0.1:8787"};
        var code=new TextBox {MinWidth=300,MinHeight=42,PlaceholderText="DEMO-KASSE-01"};
        var token=new TextBox {MinWidth=300,MinHeight=42,PasswordChar='●',PlaceholderText="Leer lassen: gespeichertes Token behalten"};
        var enabled=new CheckBox {Content="Synchronisierung aktivieren",MinHeight=42};
        var status=new TextBlock {Text="Cloud wird geladen …",TextWrapping=TextWrapping.Wrap};
        var section=Section("Cloud-Verbindung");
        Form(section,"Server-URL",url,"Gleicher PC: http://127.0.0.1:8787. Andere Server benötigen HTTPS.");
        Form(section,"Gerätecode",code,"Das Ziel bleibt für wartende Daten fest zugeordnet.");
        Form(section,"Gerätetoken",token,"Verschlüsselt für dieses Windows-Benutzerkonto gespeichert. Nicht im Chat teilen.");
        section.Children.Add(enabled);
        var save=new Button {Content="CLOUD SPEICHERN",MinHeight=46};
        var test=new Button {Content="VERBINDUNG PRÜFEN",MinHeight=46};
        var stock=new Button {Content="JETZT VOLLSTÄNDIG ABGLEICHEN",MinHeight=46};
        var refresh=new Button {Content="SYNCHRONISIEREN / STATUS",MinHeight=46};
        var buttons=new[]{save,test,stock,refresh};
        async Task Run(Func<TorCloudSyncService,Task> action,string message="")
        {
            if(!_currentUser.IsAdmin || App.CloudSync is not {} cloud)return;
            foreach(var button in buttons)button.IsEnabled=false;
            try {await action(cloud);status.Text=message+await cloud.StatusAsync();}
            catch(Exception ex){status.Text="Cloud: "+ex.Message;}
            finally {token.Text="";foreach(var button in buttons)button.IsEnabled=true;}
        }
        save.Click+=async (_,_)=>await Run(async cloud=>{
            await cloud.SaveAsync(url.Text??"",code.Text??"",token.Text??"",enabled.IsChecked==true);
            await _audit.WriteAsync(_currentUser.Username,"CLOUD_CONFIG_SAVE","SETTINGS","","Cloud-Konfiguration gespeichert; keine Zugangsdaten protokolliert.");
        });
        test.Click+=async (_,_)=>{
            await Run(async cloud=>{await cloud.TestAsync();},"Verbindung bestätigt · ");
            // Detailed last transfer status remains visible; ping is only reachability.
        };
        stock.Click+=async (_,_)=>await Run(async cloud=>{await cloud.QueueStockAsync();await cloud.SyncOnceAsync();});
        refresh.Click+=async (_,_)=>await Run(async cloud=>await cloud.SyncOnceAsync());
        foreach(var button in buttons)section.Children.Add(button);
        section.Children.Add(new TextBlock {Text="Erst CLOUD SPEICHERN. Verbindung, Bestand und Status verwenden die gespeicherten Einstellungen.",TextWrapping=TextWrapping.Wrap});
        section.Children.Add(status);page.Children.Add(section);
        var info=Section("Übertragung");
        info.Children.Add(new TextBlock {Text="Kassenmeldung automatisch jede Minute. TOR POS gleicht Artikel/Bestand nach Verkauf, Artikelpflege und Inventur automatisch im Hintergrund ab; zusätzlich erfolgt beim Start und stündlich ein Reparatur-Snapshot. " +
            "Mehrere schnelle Änderungen werden zu einem aktuellen Bestands-Snapshot zusammengefasst, damit Offline-Zeiten keine unnötige Warteschlange erzeugen. " +
            "JETZT VOLLSTÄNDIG ABGLEICHEN bleibt für eine bewusste Sofortprüfung erhalten. Neue echte Verkäufe werden weiterhin als eigene, unveränderbare Umsatzereignisse vorgemerkt. " +
            "Bei Internetfehlern bleiben Ereignisse lokal gespeichert; Kassieren wartet niemals auf das Internet. Deaktivieren pausiert nur die Übertragung. Drucker-/TSE-Bereitschaft wird nicht aus einer Cloud-Verbindung abgeleitet.",TextWrapping=TextWrapping.Wrap});
        page.Children.Add(info);
        page.AttachedToVisualTree+=async (_,_)=>{
            try {
                if(App.CloudSync is not {} cloud){status.Text="Cloud-Dienst nicht verfügbar";return;}
                var config=await cloud.ConfigurationAsync();
                url.Text=config?.BaseUrl??"http://127.0.0.1:8787";code.Text=config?.DeviceCode??"DEMO-KASSE-01";enabled.IsChecked=config?.Enabled??false;
                status.Text=await cloud.StatusAsync();
            }catch(Exception ex){status.Text="Cloud: "+ex.Message;}
        };
        Closed+=(_,_)=>token.Text="";
        return page;
    }

    private Control PrinterSelection(string title, string prefix)
    {
        var section = Section(title);
        section.Children.Add(ToggleRow(Check(prefix + ".enabled", title + " verwenden")));
        var name = Text(prefix + ".name");
        name.IsReadOnly = true;
        Form(section, "Ausgewählter Windows-Drucker", name);
        var list = new ComboBox { MinWidth = 280, MinHeight = 42, PlaceholderText = "Liste laden und Drucker wählen" };
        var refresh = new Button { Content = "LISTE AKTUALISIEREN", MinHeight = 46 };
        var test = new Button { Content = "TESTDRUCK", MinHeight = 46 };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = "Noch nicht geprüft. Auswahl und Test bestätigen keinen Papierausdruck." };
        bool loading = false;
        list.SelectionChanged += (_, _) => { if (!loading && list.SelectedItem is string selected) name.Text = selected; };
        refresh.Click += async (_, _) =>
        {
            refresh.IsEnabled = false; list.IsEnabled = false; test.IsEnabled = false;
            try
            {
                var names = await Task.Run(() => _receiptPrinter.GetInstalledPrinterNames()).WaitAsync(TimeSpan.FromSeconds(10));
                loading = true;
                list.ItemsSource = names;
                list.SelectedItem = names.FirstOrDefault(x => string.Equals(x, name.Text, StringComparison.OrdinalIgnoreCase));
                status.Text = names.Count == 0 ? "Keine Windows-Drucker gefunden. Drucker zuerst in Windows installieren." :
                    $"{names.Count} Drucker gefunden. Gewünschten Drucker auswählen, testen und SPEICHERN drücken.";
            }
            catch (Exception ex) { CrashLog.WriteException("Printer discovery", ex); status.Text = "Suche fehlgeschlagen: " + ex.Message; }
            finally { loading = false; refresh.IsEnabled = true; list.IsEnabled = true; test.IsEnabled = true; }
        };
        test.Click += async (_, _) =>
        {
            var selected = name.Text?.Trim() ?? "";
            if (selected.Length == 0) { status.Text = "Zuerst einen Drucker aus der Liste auswählen."; return; }
            refresh.IsEnabled = false; test.IsEnabled = false; list.IsEnabled = false;
            try
            {
                using var probeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                var probe = await _receiptPrinter.ProbeAsync(selected, probeTimeout.Token);
                if (!probe.Success)
                {
                    status.Text = "Drucker nicht bereit: " + probe.Message + " · Verbindung, Strom, Papier und Windows-Druckerstatus prüfen.";
                    if (_text.TryGetValue(prefix + ".last_error", out var probeError)) probeError.Text = probe.Message;
                    return;
                }

                await _receiptPrinter.PrintTestAsync(selected);
                status.Text = $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} · An Windows übergeben. Papierausdruck am Gerät kontrollieren.";
                if (_text.TryGetValue(prefix + ".last_error", out var error)) error.Text = "";
            }
            catch (Exception ex)
            {
                CrashLog.WriteException("Printer test", ex);
                var uncertain = ex is TimeoutException ||
                    ex.Message.Contains("unklar", StringComparison.OrdinalIgnoreCase) ||
                    ex.Message.Contains("Timeout", StringComparison.OrdinalIgnoreCase);
                status.Text = uncertain
                    ? "Druckstatus unklar. Nicht blind erneut drucken; zuerst Windows-Druckwarteschlange und Papierbeleg prüfen."
                    : "Drucker nicht bereit. Verbindung, Strom, Papier und Windows-Druckerstatus prüfen.";
                if (_text.TryGetValue(prefix + ".last_error", out var error)) error.Text = ex.Message;
            }
            finally
            {
                if (_text.TryGetValue(prefix + ".last_test", out var last)) last.Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
                refresh.IsEnabled = true; test.IsEnabled = true; list.IsEnabled = true;
            }
        };
        section.Children.Add(list);
        section.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { refresh, test } });
        section.Children.Add(status);
        if (prefix == "device.a4_printer") section.Children.Add(new TextBlock { Text = "Berichte werden als PDF geöffnet; den A4-Drucker im PDF-Druckdialog auswählen.", TextWrapping = TextWrapping.Wrap });
        return section;
    }

    private Control LabeledStatusRow(string label, TextBlock value)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("230,*"),
            ColumnSpacing = 14
        };
        grid.Children.Add(new TextBlock
        {
            Text = label,
            Opacity = 0.60,
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetColumn(value, 1);
        grid.Children.Add(value);
        return ToggleRow(grid);
    }

    private async Task RefreshLegalStatusAsync()
    {
        try
        {
            var report = await _compliance.CheckAsync();

            _legalMode.Text = report.ProductionAllowed
                ? "PRODUKTIV · FISKAL FREIGEGEBEN"
                : $"TESTBETRIEB · PRODUKTIV GESPERRT · {report.BlockingCount} offene Pflichtpunkte";
            _legalMode.Foreground = report.ProductionAllowed ? AppTheme.AccentTeal : AppTheme.WarningAmber;

            _legalEas.Text = report.EasSerial;

            string State(string code)
            {
                var item = report.Items.First(x => x.Code == code);
                return (item.Ready ? "OK · " : "NICHT BEREIT · ") + item.Detail;
            }

            _legalTse.Text = State("TSE");
            _legalDsfinvk.Text = State("DSFINVK");
            _legalReceipt.Text = State("RECEIPT");
            _legalParken.Text = State("PARKEN_TSE");
            _legalPfand.Text = State("PFAND");
        }
        catch (Exception ex)
        {
            _legalMode.Text = "FISKALSTATUS FEHLER";
            _legalMode.Foreground = AppTheme.WarningAmber;
            StatusText.Text = ex.Message;
        }
    }

    private Control ScannerPage()
    {
        var page = Page("Barcode-Scanner",
            "Für den Betreiber bleiben nur die Reaktionen bei unbekannten Barcodes sichtbar.");
        var section = Section("Verhalten");
        section.Children.Add(ToggleRow(Check("scanner.unknown_dialog", "Meldung bei unbekanntem Barcode anzeigen")));
        section.Children.Add(ToggleRow(Check("scanner.unknown_beep", "Akustische Meldung bei unbekanntem Barcode vorbereiten")));
        page.Children.Add(section);
        page.Children.Add(InfoCard("Performance", "Bekannte Barcodes werden aus einem RAM-Index gelesen; während des Scannens ist keine Datenbanksuche nötig.", AppTheme.InfoCardBg));
        return page;
    }

    private Control ReportsEmailPage()
    {
        var page = Page("Berichte & E-Mail",
            "Alle Standardberichte können als PDF gespeichert werden. Optional verschickt TOR POS einmal pro Monat automatisch ein PDF-Paket per E-Mail.");

        var delivery = Section("Monatlicher PDF-Versand");
        delivery.Children.Add(ToggleRow(Check("reports.email.monthly.enabled", "Monatliche Berichte automatisch per E-Mail senden")));
        Form(delivery, "Empfänger-E-Mail", Text("reports.email.recipient"), "Diese Adresse erhält das monatliche PDF-Paket.");
        Form(delivery, "Versandtag", Text("reports.email.monthly.day"), "1–28 · Standard 1. Versendet den abgeschlossenen Vormonat.");
        Form(delivery, "Versandzeit", Text("reports.email.monthly.time"), "HH:mm · Standard 00:15. Bei ausgeschaltetem TOR POS wird der verpasste Versand nach dem nächsten Start nachgeholt.");
        delivery.Children.Add(ReadOnlyRow("Sicherheitsregel", "Der E-Mail-Versand erzeugt KEINEN Z-Bericht, keinen Tagesabschluss und keinen Kassenabschluss."));
        page.Children.Add(delivery);

        var google = Section("Google-Konto / Gmail API (empfohlen)");
        google.Children.Add(new TextBlock
        {
            Text = "Kein Gmail-Passwort und kein App-Passwort in TOR POS: MIT GOOGLE ANMELDEN erzeugt einen einmaligen QR-Code. Der Inhaber scannt ihn mit dem Handy und bestätigt die Berechtigung direkt bei Google.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.78
        });
        google.Children.Add(_googleMailStatus);

        var googleConnect = new Button { Content = "MIT GOOGLE ANMELDEN (QR)", MinHeight = 48, MinWidth = 230, FontWeight = FontWeight.Bold };
        var googleTest = new Button { Content = "GOOGLE TEST-E-MAIL SENDEN", MinHeight = 46, MinWidth = 230, FontWeight = FontWeight.SemiBold };
        var googleDisconnect = new Button { Content = "GOOGLE-VERBINDUNG TRENNEN", MinHeight = 44, MinWidth = 230 };
        var googleButtons = new[] { googleConnect, googleTest, googleDisconnect };

        googleConnect.Click += async (_, _) =>
        {
            if (App.CloudSync is not { } cloud) { _googleMailStatus.Text = "TOR POS Cloud-Dienst ist nicht verfügbar."; return; }
            foreach (var b in googleButtons) b.IsEnabled = false;
            try
            {
                _googleMailStatus.Text = "Sicherer QR-Code wird erstellt …";
                using var gmail = new GoogleGmailService(_settings, cloud);
                var pairing = await gmail.StartPairingAsync();
                var ok = await new GooglePairingWindow(gmail, pairing).ShowDialog<bool>(this);
                await RefreshGoogleMailStatusAsync();
                StatusText.Text = ok ? "Google-Konto erfolgreich verbunden." : "Google-Anmeldung beendet.";
            }
            catch (Exception ex)
            {
                _googleMailStatus.Text = "Google-Anmeldung konnte nicht gestartet werden:\n" + ex.Message;
                StatusText.Text = "Google-Anmeldung fehlgeschlagen.";
            }
            finally { foreach (var b in googleButtons) b.IsEnabled = true; }
        };

        googleTest.Click += async (_, _) =>
        {
            if (App.CloudSync is not { } cloud) { _googleMailStatus.Text = "TOR POS Cloud-Dienst ist nicht verfügbar."; return; }
            foreach (var b in googleButtons) b.IsEnabled = false;
            try
            {
                using var gmail = new GoogleGmailService(_settings, cloud);
                StatusText.Text = "Google Test-E-Mail wird gesendet …";
                await gmail.SendTestAsync(_text["reports.email.recipient"].Text ?? "");
                _googleMailStatus.Text = "Google-Verbindung aktiv ✓\nTest-E-Mail wurde über Gmail API gesendet.";
                StatusText.Text = "Google Test-E-Mail erfolgreich gesendet.";
            }
            catch (Exception ex)
            {
                _googleMailStatus.Text = "Google Test-E-Mail fehlgeschlagen:\n" + ex.Message;
                StatusText.Text = "Google Test-E-Mail fehlgeschlagen.";
            }
            finally { foreach (var b in googleButtons) b.IsEnabled = true; }
        };

        googleDisconnect.Click += async (_, _) =>
        {
            if (App.CloudSync is not { } cloud) { _googleMailStatus.Text = "TOR POS Cloud-Dienst ist nicht verfügbar."; return; }
            if (!await ConfirmSimpleAsync("Google-Verbindung trennen", "Google-Zugriff für diese Kasse wirklich widerrufen? Danach wird SMTP als Fallback verwendet.")) return;
            foreach (var b in googleButtons) b.IsEnabled = false;
            try
            {
                using var gmail = new GoogleGmailService(_settings, cloud);
                await gmail.DisconnectAsync();
                await RefreshGoogleMailStatusAsync();
                StatusText.Text = "Google-Verbindung getrennt.";
            }
            catch (Exception ex)
            {
                _googleMailStatus.Text = "Google-Verbindung konnte nicht getrennt werden:\n" + ex.Message;
                StatusText.Text = "Google-Verbindung trennen fehlgeschlagen.";
            }
            finally { foreach (var b in googleButtons) b.IsEnabled = true; }
        };

        google.Children.Add(googleConnect);
        google.Children.Add(googleTest);
        google.Children.Add(googleDisconnect);
        google.Children.Add(new TextBlock
        {
            Text = "Voraussetzung für QR-Anmeldung: TOR POS Cloud muss unter Geräte eingerichtet sein und der Cloud-Server benötigt eine freigegebene Google-OAuth-Konfiguration. Die Monatsberichte selbst gehen direkt von diesem PC an die Gmail API; die PDF-Dateien werden nicht über TOR POS Cloud geleitet.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.68
        });
        page.Children.Add(google);

        var smtp = Section("E-Mail-Ausgang / SMTP (Fallback)");
        Form(smtp, "Absender-E-Mail", Text("reports.email.sender"));
        Form(smtp, "SMTP-Server", Text("reports.email.smtp.host"), "Für Gmail: smtp.gmail.com");
        Form(smtp, "SMTP-Port", Text("reports.email.smtp.port"), "Für Gmail: 587 (STARTTLS).");
        smtp.Children.Add(ToggleRow(Check("reports.email.smtp.ssl", "TLS/SSL verwenden")));
        Form(smtp, "SMTP-Benutzer", Text("reports.email.smtp.user"), "Bei Gmail die vollständige Gmail-/Google-Workspace-Adresse.");
        Form(smtp, "SMTP App-Passwort", _reportSmtpPassword, "Leer lassen, um ein bereits gespeichertes App-Passwort zu behalten. Google-App-Passwort-Leerzeichen werden automatisch entfernt.");

        var gmailPreset = new Button { Content = "GMAIL-STANDARD ÜBERNEHMEN", MinHeight = 42, MinWidth = 230, FontWeight = FontWeight.SemiBold };
        gmailPreset.Click += (_,_) =>
        {
            _text["reports.email.smtp.host"].Text = "smtp.gmail.com";
            _text["reports.email.smtp.port"].Text = "587";
            _check["reports.email.smtp.ssl"].IsChecked = true;
            var user = (_text["reports.email.smtp.user"].Text ?? "").Trim();
            var sender = (_text["reports.email.sender"].Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(sender))
                _text["reports.email.smtp.user"].Text = sender;
            else if (string.IsNullOrWhiteSpace(sender) && !string.IsNullOrWhiteSpace(user))
                _text["reports.email.sender"].Text = user;
            StatusText.Text = "Gmail-Standard gesetzt: smtp.gmail.com · Port 587 · STARTTLS";
        };
        smtp.Children.Add(gmailPreset);

        var smtpDetails = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 92,
            Text = "Noch kein E-Mail-Test ausgeführt."
        };

        var test = new Button { Content = "TEST-E-MAIL SENDEN", MinHeight = 46, MinWidth = 210, FontWeight = FontWeight.Bold };
        test.Click += async (_,_) =>
        {
            if (!test.IsEnabled) return;
            test.IsEnabled = false;
            try
            {
                if (!int.TryParse(_text["reports.email.smtp.port"].Text, out var port)) port = 587;
                var service = new ReportEmailService(
                    _settings,
                    new BusinessManagementService(new SqliteDatabase(AppPaths.DatabasePath), _settings, _audit));
                var password = ReportEmailService.ResolveAppPassword(_reportSmtpPassword.Text, _reportSmtpPasswordProtected);
                StatusText.Text = "Test-E-Mail wird gesendet ...";
                smtpDetails.Text = "SMTP-Verbindung wird geprüft ...";
                await service.SendTestAsync(
                    _text["reports.email.recipient"].Text ?? "",
                    _text["reports.email.sender"].Text ?? "",
                    _text["reports.email.smtp.host"].Text ?? "",
                    port,
                    _check["reports.email.smtp.ssl"].IsChecked == true,
                    _text["reports.email.smtp.user"].Text ?? "",
                    password);
                StatusText.Text = "Test-E-Mail erfolgreich gesendet.";
                smtpDetails.Text = "ERFOLG: Test-E-Mail wurde gesendet.\nSTARTTLS, TLS-Handshake und SMTP-Anmeldung funktionieren.";
            }
            catch (Exception ex)
            {
                StatusText.Text = "E-Mail-Test fehlgeschlagen. Details im SMTP-Diagnosefeld.";
                smtpDetails.Text = ex.Message;
            }
            finally { test.IsEnabled = true; }
        };
        smtp.Children.Add(test);
        Form(smtp, "SMTP-Diagnose", smtpDetails, "App-Passwort wird niemals in diesem Feld oder im Audit-Log ausgegeben.");
        page.Children.Add(smtp);

        var state = Section("Letzter automatischer Versand");
        var lastPeriod = Text("reports.email.monthly.last_period"); lastPeriod.IsReadOnly = true;
        Form(state, "Letzter Berichtsmonat", lastPeriod);
        var lastSuccess = Text("reports.email.monthly.last_success"); lastSuccess.IsReadOnly = true;
        Form(state, "Letzter erfolgreicher Versand", lastSuccess);
        var lastError = Text("reports.email.monthly.last_error", true); lastError.IsReadOnly = true; lastError.MinHeight = 76;
        Form(state, "Letzter Fehler", lastError);
        var lastFolder = Text("reports.email.monthly.last_folder"); lastFolder.IsReadOnly = true;
        Form(state, "Lokales PDF-Paket", lastFolder, "Die versendeten PDFs bleiben zusätzlich lokal im Reports-Ordner gespeichert.");
        page.Children.Add(state);

        page.Children.Add(InfoCard(
            "Welche PDFs werden monatlich versendet?",
            "Monatsübersicht, Kassenjournal, Verkaufsstatistik, Bedienerabrechnung, Stornobericht, vorhandenes Z-Archiv und ein aktueller Warenbestands-Snapshot. Es wird niemals automatisch ein neuer Z-Abschluss erzeugt.",
            AppTheme.InfoCardBg));
        return page;
    }

    private Control BackupPage()
    {
        var page = Page("Datensicherung",
            "Backups sind vom Verkaufspfad getrennt und können auf ein anderes Laufwerk oder einen Serverordner geschrieben werden.");

        var section = Section("Backup");
        section.Children.Add(ToggleRow(Check("backup.on_exit", "Beim Programmschluss Sicherung erstellen")));
        Form(section, "Sicherungsverzeichnis", Text("backup.directory"),
            $"Leer = Standardordner: {AppPaths.BackupsPath}");
        Form(section, "Sicherungen behalten", Text("backup.keep_count"));
        page.Children.Add(section);

        var daily = Section("Tägliche automatische Sicherung");
        daily.Children.Add(ToggleRow(Check("backup.daily.enabled", "Tägliche automatische Sicherung aktiv")));
        Form(daily, "Uhrzeit", Text("backup.daily.time"), "HH:mm · Standard 00:00. Erzeugt keinen Z-Bericht und keinen Kassenabschluss.");
        var dailyLast = Text("backup.daily.last_success"); dailyLast.IsReadOnly = true;
        Form(daily, "Letzte erfolgreiche Sicherung", dailyLast, "Wenn TOR POS zur Uhrzeit geschlossen war, wird die verpasste Sicherung beim nächsten Start nachgeholt.");
        var dailyError = Text("backup.daily.last_error", true); dailyError.IsReadOnly = true; dailyError.MinHeight = 76;
        Form(daily, "Letzter Fehler", dailyError, "Bei nicht erreichbarem Laufwerk bleibt der Erfolgszeitpunkt unverändert; TOR versucht nach fünf Minuten erneut.");
        var dailyPath = Text("backup.daily.last_path"); dailyPath.IsReadOnly = true;
        Form(daily, "Letzte Datei", dailyPath);
        page.Children.Add(daily);

        var actions = Section("Prüfen & sichern");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };

        var test = new Button { Content = "VERZEICHNIS TESTEN", MinHeight = 44, MinWidth = 180 };
        test.Click += (_,_) =>
        {
            _backup.TestDirectory(_text["backup.directory"].Text, out var message);
            StatusText.Text = message;
        };

        var create = new Button { Content = "SICHERUNG JETZT ERSTELLEN", MinHeight = 44, MinWidth = 220 };
        create.Click += async (_,_) =>
        {
            try
            {
                StatusText.Text = "Sicherung wird erstellt ...";
                var path = await _backup.CreateBackupAsync(_text["backup.directory"].Text);
                path = await new BackupEncryptionService(_settings).EncryptIfEnabledAsync(path);
                StatusText.Text = $"Sicherung erstellt: {path}";
            }
            catch (Exception ex)
            {
                StatusText.Text = $"Sicherung fehlgeschlagen: {ex.Message}";
            }
        };

        var full = new Button { Content="DATEN + BILDER SICHERN / PRÜFEN",MinHeight=44 };
        full.Click+=async(_,_)=>{
            if(!full.IsEnabled)return;full.IsEnabled=false;string? restored=null;string? package=null;string? decrypted=null;
            try{
                StatusText.Text="Sicherung und Wiederherstellungsprüfung laufen ...";
                var service=new FullBackupService(new SqliteDatabase(AppPaths.DatabasePath),AppPaths.DataDirectory);
                package=await service.CreateAsync(_backup.ResolveDirectory(_text["backup.directory"].Text));
                var encryption=new BackupEncryptionService(_settings);
                package=await encryption.EncryptIfEnabledAsync(package);
                var toVerify=package;
                if(BackupEncryptionService.LooksEncrypted(package))
                {
                    decrypted=Path.Combine(Path.GetTempPath(),"tor-restore-check-"+Guid.NewGuid().ToString("N")+".zip");
                    // Self-test runs on the same machine that just created the backup, so the
                    // DPAPI-wrapped key always unwraps automatically here; no recovery code needed.
                    await BackupEncryptionService.DecryptAsync(package,decrypted,recoveryCode:null);
                    toVerify=decrypted;
                }
                restored=Path.Combine(Path.GetTempPath(),"tor-restore-check-"+Guid.NewGuid().ToString("N"));
                var result=await FullBackupService.VerifyRestoreAsync(toVerify,restored);
                StatusText.Text=$"Geprüft: {result.Files} Dateien · {result.Products} Artikel · {result.Sales} Verkäufe · {package}";
            }catch(Exception ex){StatusText.Text="Prüfung fehlgeschlagen: "+ex.Message+(package is null?"":" · Paket: "+package);}
            finally{
                try{if(restored is not null&&Directory.Exists(restored))Directory.Delete(restored,true);}catch(Exception ex){CrashLog.WriteException("Backup check cleanup",ex);}
                try{if(decrypted is not null&&File.Exists(decrypted))File.Delete(decrypted);}catch(Exception ex){CrashLog.WriteException("Backup check cleanup",ex);}
                full.IsEnabled=true;
            }
        };
        actions.Children.Add(full);
        buttons.Children.Add(test);
        buttons.Children.Add(create);
        actions.Children.Add(buttons);
        page.Children.Add(actions);

        page.Children.Add(BackupEncryptionSection());

        return page;
    }

    private Control BackupEncryptionSection()
    {
        var section = Section("Sicherung verschlüsseln");
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Opacity = 0.85 };
        section.Children.Add(status);

        var enableButton = new Button { Content = "VERSCHLÜSSELUNG AKTIVIEREN", MinHeight = 44 };
        var regenerateButton = new Button { Content = "WIEDERHERSTELLUNGSCODE NEU ERSTELLEN", MinHeight = 44, IsVisible = false };
        var disableButton = new Button { Content = "VERSCHLÜSSELUNG DEAKTIVIEREN", MinHeight = 44, IsVisible = false };

        async Task RefreshAsync()
        {
            var encryption = new BackupEncryptionService(_settings);
            var current = await encryption.GetStatusAsync();
            if (current.Enabled)
            {
                status.Text =
                    $"Aktiv · Wiederherstellungscode-Kennung {current.RecoveryFingerprint}. " +
                    "Neue Sicherungen (manuell und täglich automatisch) werden verschlüsselt. " +
                    "Auf diesem Computer wird automatisch entschlüsselt; auf einem anderen Computer wird der " +
                    "Wiederherstellungscode benötigt." +
                    // R122: an existing installation keeps the old key derivation
                    // until a NEW code is generated - the old one cannot be
                    // converted, because the code itself was never stored.
                    (current.UsesLegacyKeyDerivation
                        ? "\n\nHINWEIS: Dieser Wiederherstellungscode stammt aus einer älteren Version und verwendet " +
                          "die frühere Schlüsselableitung. Vorhandene Sicherungen bleiben uneingeschränkt " +
                          "wiederherstellbar. Für das aktuelle Verfahren einmal WIEDERHERSTELLUNGSCODE NEU ERSTELLEN " +
                          "wählen und den neuen Code sicher notieren."
                        : "");
                enableButton.IsVisible = false;
                regenerateButton.IsVisible = true;
                disableButton.IsVisible = true;
            }
            else
            {
                status.Text =
                    "Nicht aktiv · Sicherungen werden unverschlüsselt geschrieben. " +
                    "Bei Aktivierung wird ein Wiederherstellungscode einmalig angezeigt - ohne diesen Code " +
                    "kann eine Sicherung nach einem Totalausfall dieses Computers nicht wiederhergestellt werden.";
                enableButton.IsVisible = true;
                regenerateButton.IsVisible = false;
                disableButton.IsVisible = false;
            }
        }

        enableButton.Click += async (_, _) =>
        {
            var encryption = new BackupEncryptionService(_settings);
            var code = await encryption.EnableAndGenerateRecoveryCodeAsync();
            await ShowRecoveryCodeAsync(code, isFirstSetup: true);
            await _audit.WriteAsync(_currentUser.Username, "BACKUP_ENCRYPTION_ENABLED", "SETTINGS", "", "Backup-Verschlüsselung aktiviert.");
            await RefreshAsync();
        };

        regenerateButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmSimpleAsync(
                "Wiederherstellungscode neu erstellen",
                "Der alte Code wird ungültig für zukünftige Sicherungen. Bereits erstellte Sicherungen bleiben nur mit dem Code entschlüsselbar, der zum Zeitpunkt ihrer Erstellung gültig war (oder automatisch auf diesem Computer).");
            if (!confirmed) return;
            var encryption = new BackupEncryptionService(_settings);
            var code = await encryption.EnableAndGenerateRecoveryCodeAsync();
            await ShowRecoveryCodeAsync(code, isFirstSetup: false);
            await _audit.WriteAsync(_currentUser.Username, "BACKUP_ENCRYPTION_CODE_REGENERATED", "SETTINGS", "", "Wiederherstellungscode neu erstellt.");
            await RefreshAsync();
        };

        disableButton.Click += async (_, _) =>
        {
            var confirmed = await ConfirmSimpleAsync(
                "Verschlüsselung deaktivieren",
                "Neue Sicherungen werden wieder unverschlüsselt geschrieben. Bereits verschlüsselte Sicherungen bleiben mit dem vorhandenen Wiederherstellungscode entschlüsselbar.");
            if (!confirmed) return;
            var encryption = new BackupEncryptionService(_settings);
            await encryption.DisableAsync();
            await _audit.WriteAsync(_currentUser.Username, "BACKUP_ENCRYPTION_DISABLED", "SETTINGS", "", "Backup-Verschlüsselung deaktiviert.");
            await RefreshAsync();
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        row.Children.Add(enableButton);
        row.Children.Add(regenerateButton);
        row.Children.Add(disableButton);
        section.Children.Add(row);

        section.AttachedToVisualTree += async (_, _) => await RefreshAsync();

        return section;
    }

    private async Task ShowRecoveryCodeAsync(string code, bool isFirstSetup)
    {
        var codeBox = new TextBox
        {
            Text = code,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas,Courier New,monospace"),
            FontSize = 22,
            MinHeight = 46,
            TextAlignment = Avalonia.Media.TextAlignment.Center
        };
        var confirm = new CheckBox { Content = "Ich habe diesen Code sicher gespeichert." };
        var ok = new Button { Content = "FERTIG", MinHeight = 46, IsEnabled = false };
        confirm.IsCheckedChanged += (_, _) => ok.IsEnabled = confirm.IsChecked == true;

        var dialog = new Window
        {
            Title = "Wiederherstellungscode",
            Width = 520,
            Height = 340,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ok.Click += (_, _) => dialog.Close();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = "Wiederherstellungscode", FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = (isFirstSetup
                        ? "Dieser Code wird nur jetzt angezeigt und nirgendwo in TOR POS gespeichert. "
                        : "Der bisherige Code wird für zukünftige Sicherungen ungültig. Dieser neue Code wird nur jetzt angezeigt. ") +
                        "Ohne diesen Code kann eine Sicherung nach einem Totalausfall dieses Computers NICHT wiederhergestellt werden. Bitte an einem sicheren, separaten Ort aufbewahren (ausgedruckt, Passwort-Tresor, Händlerportal).",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                },
                codeBox,
                confirm,
                ok
            }
        };

        await dialog.ShowDialog(this);
    }

    private Control PermissionsPage()
    {
        var page = Page("Personal & Rechte",
            "Drei Mitarbeiterkonten mit eigenen Zugangsdaten und einzeln einstellbaren Funktionsrechten.");

        var section = Section("Admin + 3 Mitarbeiter");
        section.Children.Add(ReadOnlyRow("Admin", "Vollzugriff · Benutzerverwaltung nur durch Admin"));
        section.Children.Add(ReadOnlyRow("Mitarbeiter", "Genau 3 Konten · einzeln aktivierbar"));

        var manage = new Button
        {
            Content = "3 BENUTZER & RECHTE VERWALTEN",
            MinWidth = 300,
            MinHeight = 52,
            FontWeight = FontWeight.Bold
        };
        manage.Click += async (_,_) =>
        {
            await new UserManagementWindow(_authentication, _currentUser)
                .ShowDialog<bool>(this);
        };
        section.Children.Add(manage);
        page.Children.Add(section);

        page.Children.Add(InfoCard(
            "Sichere Rechte",
            "Verkauf, Rabatt, Sofort-Storno, Bon-Storno, Parken, Einlage/Entnahme, Z-Bericht, Stammdaten, Bon-Historie und Training werden pro Mitarbeiter freigegeben. Einstellungen bleiben immer Admin-only.",
            AppTheme.InfoCardBg));

        page.Children.Add(TrainingAccessSection());

        return page;
    }

    /// <summary>
    /// R122: the training entry code used to be the hard-coded "0000", printed
    /// on the login screen next to its own input field. Training mode books
    /// nothing and cannot use the card terminal, so this is about who can walk
    /// up and open a session, not about fiscal integrity - but it should still
    /// be the operator's choice, not a constant in the source.
    /// </summary>
    private Control TrainingAccessSection()
    {
        var section = Section("Trainingszugang");
        var codeBox = new TextBox
        {
            MinWidth = 220,
            MinHeight = 42,
            MaxLength = 4,
            PlaceholderText = "4 Ziffern"
        };
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "TRAINING-CODE SPEICHERN", MinHeight = 46 };

        async Task LoadAsync()
        {
            var current = await _settings.GetAsync(
                TrainingAccessPolicy.SettingKey,
                TrainingAccessPolicy.FactoryCode);
            codeBox.Text = TrainingAccessPolicy.Effective(current);
            status.Text = TrainingAccessPolicy.IsFactoryDefault(current)
                ? "Aktuell gilt der Auslieferungscode 0000. Solange er gilt, steht er auch auf der Anmeldeseite."
                : "Ein eigener Code ist gesetzt. Die Anmeldeseite nennt ihn nicht mehr.";
        }

        save.Click += async (_,_) =>
        {
            if (!_currentUser.IsAdmin) return;
            var typed = (codeBox.Text ?? "").Trim();
            if (!TrainingAccessPolicy.IsValidCode(typed))
            {
                status.Text = "Der Training-Code muss aus genau 4 Ziffern bestehen. Nicht gespeichert.";
                return;
            }

            await _settings.SaveManyAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
            {
                [TrainingAccessPolicy.SettingKey] = typed
            });
            // The code itself is deliberately NOT written to the audit log.
            await _audit.WriteAsync(_currentUser.Username, "TRAINING_CODE_CHANGED", "SETTINGS", "",
                "Training-Zugangscode geändert; der Code selbst wird nicht protokolliert.");
            await LoadAsync();
            status.Text = "Gespeichert. " + status.Text;
        };

        Form(section, "Training-Code", codeBox,
            "Gilt nur für die Trainingsanmeldung ohne Benutzerpasswort. Trainingsverkäufe werden nie gebucht, signiert oder an die Cloud gemeldet.");
        section.Children.Add(save);
        section.Children.Add(status);
        _ = LoadAsync();
        return section;
    }


private Control TsePage()
{
    var runtime = _tseProvider.GetRuntimeStatus();

    var page = Page(
        "TSE-Aktivierung",
        "TOR POS verwendet Swissbit Hardware-TSE über die offizielle WORM API. " +
        "Die Swissbit SDK-Dateien werden von TOR nicht mitgeliefert und müssen aus einem offiziell bezogenen SDK-Paket eingebunden werden.");

    var standard = Section("TOR Standard-TSE");
    standard.Children.Add(ReadOnlyRow("Hersteller", "Swissbit"));
    standard.Children.Add(ReadOnlyRow("Standardprodukt", "Swissbit Hardware TSE 2"));
    standard.Children.Add(ReadOnlyRow("Anschluss", "USB / Windows-Laufwerk"));
    standard.Children.Add(ReadOnlyRow("TOR Provider", "SWISSBIT_HARDWARE"));
    standard.Children.Add(ReadOnlyRow(
        "SDK geladen",
        runtime.SdkLoaded ? "JA" : "NEIN"));
    standard.Children.Add(ReadOnlyRow(
        "WORM API Version",
        string.IsNullOrWhiteSpace(runtime.SdkVersion)
            ? "nicht erkannt"
            : runtime.SdkVersion));
    standard.Children.Add(ReadOnlyRow(
        "SDK-Datei",
        string.IsNullOrWhiteSpace(runtime.LibraryPath)
            ? "WormAPI.dll fehlt"
            : runtime.LibraryPath));
    standard.Children.Add(ReadOnlyRow(
        "API-Prüfung",
        runtime.RequiredApiAvailable
            ? "Grundfunktionen vorhanden"
            : "nicht vollständig"));
    standard.Children.Add(ReadOnlyRow(
        "Transaktions-API",
        _tseProvider.TransactionAvailable
            ? "Start / Update / Finish verfügbar"
            : "nicht vollständig"));
    standard.Children.Add(ReadOnlyRow(
        "TAR-Export",
        _tseProvider.ExportAvailable
            ? "verfügbar"
            : "nicht verfügbar"));
    standard.Children.Add(ReadOnlyRow(
        "Kompatibilitätsprinzip",
        "Swissbit Unified SDK · Hardware-Generationen 1 / 1.1 / 2"));
    page.Children.Add(standard);

    var status = Section("TSE-Status");
    var statusBox = Text("tse.status");
    statusBox.IsReadOnly = true;
    Form(status, "Status", statusBox);

    var lastTest = Text("tse.last_test");
    lastTest.IsReadOnly = true;
    Form(status, "Letzter Test", lastTest);

    var lastError = Text("tse.last_error");
    lastError.IsReadOnly = true;
    Form(status, "Letzter Fehler", lastError);

    page.Children.Add(status);

    var identity = Section("Automatisch aus der TSE lesen");

    var serial = Text("tse.serial");
    serial.IsReadOnly = true;

    var bsi = Text("tse.bsi_id");
    bsi.IsReadOnly = true;

    var devicePath = Text("tse.device_path");
    devicePath.IsReadOnly = true;

    var activationDate = Text("tse.activation_date");
    activationDate.IsReadOnly = true;

    var expiry = Text("tse.expiry_date");
    expiry.IsReadOnly = true;

    Form(
        identity,
        "TSE-Seriennummer",
        serial,
        "Wird direkt über die Swissbit WORM API ausgelesen.");

    Form(
        identity,
        "BSI-Zertifizierungsnummer",
        bsi,
        "Wird nicht erfunden. Falls die verwendete SDK-Version sie nicht direkt liefert, bleibt das Feld leer und wird später aus zertifizierter Produkt-/Zertifikatszuordnung ergänzt.");

    Form(identity, "Gerätepfad", devicePath);
    Form(identity, "Aktiviert am", activationDate);
    Form(identity, "Zertifikatsablauf", expiry);

    page.Children.Add(identity);

    var client = Section("Kassen-Zuordnung");

    Form(
        client,
        "Client-ID / Kassen-ID",
        Text("tse.client_id"),
        "Max. 30 ASCII-Zeichen, kein Slash. Empfehlung: TOR eAS-Seriennummer oder eindeutige Kassen-ID.");

    client.Children.Add(ToggleRow(Check(
        "tse.auto_connect",
        "Swissbit TSE beim Programmstart automatisch prüfen")));

    page.Children.Add(client);

    var credentials = Section(
        "Erst-Aktivierung – Zugangsdaten nur temporär");

    var adminPin = new TextBox
    {
        MinHeight = 38,
        PasswordChar = '*',
        MaxLength = 5,
        PlaceholderText = "5 Zeichen · wird nicht gespeichert"
    };

    var timeAdminPin = new TextBox
    {
        MinHeight = 38,
        PasswordChar = '*',
        MaxLength = 5,
        PlaceholderText = "5 Zeichen · wird nicht gespeichert"
    };

    var puk = new TextBox
    {
        MinHeight = 38,
        PasswordChar = '*',
        MaxLength = 6,
        PlaceholderText = "6 Zeichen · wird nicht gespeichert"
    };

    var credentialSeed = new TextBox
    {
        MinHeight = 38,
        PasswordChar = '*',
        PlaceholderText = "Nur Seed des TSE-Lieferanten verwenden"
    };

    Form(
        credentials,
        "Admin-PIN",
        adminPin,
        "Neue/aktuelle Admin-PIN. Nicht in TOR-Einstellungen gespeichert.");

    Form(
        credentials,
        "TimeAdmin-PIN",
        timeAdminPin,
        "Für Zeit-Synchronisation der TSE. In v0.6.8 nur für diesen Vorgang im Arbeitsspeicher.");

    Form(
        credentials,
        "Admin-PUK",
        puk,
        "Nicht speichern. Falsche PUK/Seed-Angaben können eine Produktiv-TSE dauerhaft sperren.");

    Form(
        credentials,
        "Credential-Seed",
        credentialSeed,
        "Nicht pauschal annehmen: Der Seed kann vom TSE-Lieferanten abhängen.");

    var activationConfirmation = new CheckBox
    {
        Content =
            "Ich bestätige, dass Credential-Seed, PIN und PUK zur angeschlossenen TSE gehören.",
        Margin = new Avalonia.Thickness(0, 8, 0, 0)
    };

    credentials.Children.Add(
        ToggleRow(activationConfirmation));

    page.Children.Add(credentials);

    var actions = Section("Swissbit Hardware-TSE");

    var sdkRow = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10
    };

    var actionRow = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 10,
        Margin = new Avalonia.Thickness(0, 10, 0, 0)
    };

    var findSdk = new Button
    {
        Content = "SDK AUF PC SUCHEN",
        MinHeight = 48,
        MinWidth = 190
    };

    var chooseSdk = new Button
    {
        Content = "WORMAPI.DLL AUSWÄHLEN",
        MinHeight = 48,
        MinWidth = 220
    };

    var swissbitDownload = new Button
    {
        Content = "SWISSBIT DOWNLOAD-CENTER",
        MinHeight = 48,
        MinWidth = 220
    };

    var detect = new Button
    {
        Content = "TSE SUCHEN",
        MinHeight = 48,
        MinWidth = 170
    };

    var activate = new Button
    {
        Content = "TSE AKTIVIEREN",
        MinHeight = 48,
        MinWidth = 180,
        IsEnabled = _tseProvider.ActivationAvailable
    };

    var exportTar = new Button
    {
        Content = "TSE TAR EXPORT",
        MinHeight = 48,
        MinWidth = 180,
        IsEnabled = _tseProvider.ExportAvailable
    };

    findSdk.Click += async (_,_) =>
    {
        StatusText.Text =
            "Windows wird nach einer kompatiblen Swissbit WormAPI.dll durchsucht ...";

        var libraries =
            await _tseProvider.FindSdkLibrariesAsync();

        if (libraries.Count == 0)
        {
            StatusText.Text =
                "Keine kompatible WormAPI.dll auf diesem PC gefunden. " +
                "Offizielles Swissbit SDK herunterladen oder DLL manuell auswählen.";
            return;
        }

        var configured =
            _tseProvider.ConfigureSdkLibrary(
                libraries[0]);

        activate.IsEnabled =
            _tseProvider.ActivationAvailable;

        exportTar.IsEnabled =
            _tseProvider.ExportAvailable;

        StatusText.Text =
            configured.RequiredApiAvailable
                ? $"Swissbit SDK gefunden: {configured.LibraryPath} · jetzt TSE SUCHEN drücken."
                : configured.Message;
    };

    chooseSdk.Click += async (_,_) =>
    {
        var result =
            await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title =
                        "Offizielle Swissbit WormAPI.dll auswählen",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType(
                            "Swissbit WORM API")
                        {
                            Patterns = ["*.dll"]
                        }
                    ]
                });

        var file = result.FirstOrDefault();

        if (file is null)
            return;

        var configured =
            _tseProvider.ConfigureSdkLibrary(
                file.Path.LocalPath);

        activate.IsEnabled =
            _tseProvider.ActivationAvailable;

        exportTar.IsEnabled =
            _tseProvider.ExportAvailable;

        StatusText.Text =
            configured.RequiredApiAvailable
                ? $"Swissbit SDK geladen: {configured.LibraryPath} · jetzt TSE SUCHEN drücken."
                : configured.Message;
    };

    swissbitDownload.Click += (_,_) =>
    {
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo
                {
                    FileName =
                        "https://www.swissbit.com/de/my-swissbit/download-center",
                    UseShellExecute = true
                });

            StatusText.Text =
                "Swissbit Download-Center wurde im Browser geöffnet. " +
                "TSE-Downloads können eine Anmeldung erfordern.";
        }
        catch (Exception ex)
        {
            StatusText.Text =
                "Browser konnte nicht geöffnet werden: " +
                ex.Message;
        }
    };

    detect.Click += async (_,_) =>
    {
        StatusText.Text = "Swissbit SDK und TSE werden geprüft ...";

        var result =
            await _tseProvider.ProbeAsync();

        _text["tse.status"].Text =
            result.State.ToString().ToUpperInvariant();

        _text["tse.last_test"].Text =
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        _text["tse.last_error"].Text =
            result.State is TseConnectionState.Error
                or TseConnectionState.SdkMissing
                or TseConnectionState.NotFound
                ? result.Message
                : "";

        if (result.Device is not null)
        {
            _text["tse.serial"].Text =
                result.Device.SerialNumber;

            _text["tse.bsi_id"].Text =
                result.Device.BsiCertificationId;

            _text["tse.device_path"].Text =
                result.Device.DevicePath;

            _text["tse.expiry_date"].Text =
                result.Device.CertificateValidUntil?
                    .ToString("yyyy-MM-dd") ?? "";
        }

        await _settings.SaveManyAsync(
            new Dictionary<string,string>
            {
                ["tse.status"] =
                    _text["tse.status"].Text ?? "",
                ["tse.last_test"] =
                    _text["tse.last_test"].Text ?? "",
                ["tse.last_error"] =
                    _text["tse.last_error"].Text ?? "",
                ["tse.serial"] =
                    _text["tse.serial"].Text ?? "",
                ["tse.bsi_id"] =
                    _text["tse.bsi_id"].Text ?? "",
                ["tse.device_path"] =
                    _text["tse.device_path"].Text ?? "",
                ["tse.expiry_date"] =
                    _text["tse.expiry_date"].Text ?? ""
            });

        StatusText.Text = result.Message;
    };

    activate.Click += async (_,_) =>
    {
        if (activationConfirmation.IsChecked != true)
        {
            StatusText.Text =
                "Aktivierung gesperrt: Bestätigung für Credential-Seed / PIN / PUK fehlt.";
            return;
        }

        var request = new TseActivationRequest(
            _text["tse.client_id"].Text?.Trim() ?? "",
            adminPin.Text ?? "",
            puk.Text ?? "",
            timeAdminPin.Text ?? "",
            credentialSeed.Text ?? "");

        StatusText.Text =
            "Swissbit TSE-Aktivierung läuft. TSE nicht entfernen ...";

        var result =
            await _tseProvider.ActivateAsync(request);

        adminPin.Text = "";
        timeAdminPin.Text = "";
        puk.Text = "";
        credentialSeed.Text = "";
        activationConfirmation.IsChecked = false;

        if (result.Device is not null)
        {
            _text["tse.serial"].Text =
                result.Device.SerialNumber;

            _text["tse.device_path"].Text =
                result.Device.DevicePath;

            _text["tse.expiry_date"].Text =
                result.Device.CertificateValidUntil?
                    .ToString("yyyy-MM-dd") ?? "";
        }

        var masterDataNote = "";
        if (result.Success)
        {
            _text["tse.status"].Text = "AKTIV";
            _text["tse.activation_date"].Text =
                DateTime.Now.ToString("yyyy-MM-dd");
            _text["tse.last_error"].Text = "";

            // R133: right after activation the TSE export is still small; it
            // provides certificate, public key, signature algorithm and log
            // time format for Stamm_TSE. A failure here does not undo the
            // activation - the data can be taken from any later TSE export.
            try
            {
                Directory.CreateDirectory(AppPaths.TseExportsPath);
                var activationExport = Path.Combine(AppPaths.TseExportsPath, $"TSE-Aktivierung-{DateTime.Now:yyyyMMdd-HHmmss}.tar");
                var exported = await _tseProvider.ExportTarAsync(activationExport);
                if (exported.Success)
                {
                    var serials = await new TseMasterDataRepository(new SqliteDatabase(AppPaths.DatabasePath), _audit)
                        .ImportFromTarAsync(activationExport, _currentUser.Username);
                    masterDataNote = serials.Count > 0
                        ? " · TSE-Stammdaten übernommen"
                        : " · TSE-Stammdaten nicht im Export gefunden - später über TSE-Export übernehmen";
                }
                else
                {
                    masterDataNote = " · TSE-Stammdaten später über TSE-Export übernehmen: " + exported.Message;
                }
            }
            catch (Exception ex)
            {
                masterDataNote = " · TSE-Stammdaten später über TSE-Export übernehmen: " + ex.Message;
            }
        }
        else
        {
            _text["tse.last_error"].Text =
                result.Message;
        }

        _text["tse.last_test"].Text =
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        await _settings.SaveManyAsync(
            new Dictionary<string,string>
            {
                ["tse.status"] =
                    _text["tse.status"].Text ?? "",
                ["tse.activation_date"] =
                    _text["tse.activation_date"].Text ?? "",
                ["tse.last_test"] =
                    _text["tse.last_test"].Text ?? "",
                ["tse.last_error"] =
                    _text["tse.last_error"].Text ?? "",
                ["tse.serial"] =
                    _text["tse.serial"].Text ?? "",
                ["tse.device_path"] =
                    _text["tse.device_path"].Text ?? "",
                ["tse.expiry_date"] =
                    _text["tse.expiry_date"].Text ?? ""
            });

        StatusText.Text = result.Message + masterDataNote;
    };

    exportTar.Click += async (_,_) =>
    {
        var desktop =
            Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory);

        var target = Path.Combine(
            desktop,
            $"TOR-TSE-TAR-{DateTime.Now:yyyyMMdd-HHmmss}.tar");

        StatusText.Text =
            "TSE TAR-Export läuft ...";

        var result =
            await _tseProvider.ExportTarAsync(target);

        var imported = "";
        if (result.Success)
        {
            var serials = await new TseMasterDataRepository(new SqliteDatabase(AppPaths.DatabasePath), _audit)
                .ImportFromTarAsync(target, _currentUser.Username);
            imported = serials.Count > 0 ? " · TSE-Stammdaten übernommen" : "";
        }

        StatusText.Text =
            result.Success
                ? $"TSE TAR gespeichert: {result.FilePath}{imported}"
                : result.Message;
    };

    sdkRow.Children.Add(findSdk);
    sdkRow.Children.Add(chooseSdk);
    sdkRow.Children.Add(swissbitDownload);

    actionRow.Children.Add(detect);
    actionRow.Children.Add(activate);
    actionRow.Children.Add(exportTar);

    actions.Children.Add(sdkRow);
    actions.Children.Add(actionRow);

    page.Children.Add(actions);

    page.Children.Add(InfoCard(
        "Sicherheitsregel",
        "TOR ruft bei einer teilweise veränderten, aber noch nicht initialisierten TSE nicht automatisch erneut Setup auf. " +
        "Damit wird verhindert, dass durch einen falschen Credential-Seed wiederholte PUK-Fehlversuche ausgelöst werden.",
        AppTheme.WarningAmberBg));

    page.Children.Add(InfoCard(
        "Produktivfreigabe",
        "WormAPI.dll kann jetzt real geladen, die TSE erkannt, initialisiert und per Start/Update/FinishTransaction angesprochen werden. " +
        "Trotzdem bleibt der steuerliche Produktivmodus gesperrt, bis der konkrete Swissbit Hardware-TSE-2 SDK-Stand, ProcessData/ProcessType, Parken, DSFinV-K und Belegausgabe mit realer Hardware vollständig abgenommen wurden.",
        AppTheme.WarningAmberBg));

    return page;
}

    private Control LegalPage()
    {
        var page = Page(
            "Recht & Fiskal",
            "Deutschland-Status nach AO § 146a, KassenSichV und DSFinV-K. Produktivbetrieb wird nicht per Benutzer-Schalter freigegeben.");

        var release = Section("Produktivfreigabe");
        _legalMode.FontSize = 19;
        _legalMode.FontWeight = FontWeight.Bold;
        release.Children.Add(ToggleRow(_legalMode));
        release.Children.Add(ReadOnlyRow(
            "Regel",
            "Produktivfreigabe nur nach realer TSE-Signierung, DSFinV-K-Export und Belegprüfung."));
        page.Children.Add(release);

        var identity = Section("Elektronisches Aufzeichnungssystem");
        identity.Children.Add(ReadOnlyRow("Hersteller", "TOR Kassensysteme"));
        identity.Children.Add(ReadOnlyRow("Modell", "TOR POS Pro"));
        _legalEas.TextWrapping = TextWrapping.Wrap;
        identity.Children.Add(LabeledStatusRow("eAS-Seriennummer", _legalEas));
        identity.Children.Add(ReadOnlyRow("DSFinV-K Zielversion", "2.4"));
        page.Children.Add(identity);

        var readiness = Section("Pflichtmodule");
        readiness.Children.Add(LabeledStatusRow("Swissbit TSE", _legalTse));
        readiness.Children.Add(LabeledStatusRow("DSFinV-K", _legalDsfinvk));
        readiness.Children.Add(LabeledStatusRow("Beleg § 6 KassenSichV", _legalReceipt));
        readiness.Children.Add(LabeledStatusRow("Parken / Bestellung TSE", _legalParken));
        readiness.Children.Add(LabeledStatusRow(
            InstallationEdition.ReadLocked() == "KIOSK"
                ? "Pfand-Steuerlogik"
                : "Extra-Steuerlogik",
            _legalPfand));
        page.Children.Add(readiness);

        var notification = Section("Mitteilung nach § 146a Abs. 4 AO");
        Form(
            notification,
            "Status",
            Combo("legal.kassenmeldung.status", "OFFEN", "GEMELDET", "NICHT_ERFORDERLICH"),
            "TOR übermittelt in dieser Version NICHT an Mein ELSTER / ERiC.");
        Form(
            notification,
            "Meldedatum",
            Text("legal.kassenmeldung.date"),
            "Nur Dokumentationsfeld. Eine Eingabe löst keine Finanzamt-Übermittlung aus.");
        page.Children.Add(notification);

        var exportCenter = Section("Prüfungsdaten / Export");

        var exportButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var auditExport = new Button
        {
            Content = "AUDIT-LOG EXPORTIEREN",
            MinHeight = 48,
            MinWidth = 190
        };

        var dsCheck = new Button
        {
            Content = "DSFINV-K 2.4 PRÜFEN",
            MinHeight = 48,
            MinWidth = 190
        };

        auditExport.Click += async (_,_) =>
        {
            try
            {
                var desktop =
                    Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory);

                var target =
                    Path.Combine(
                        desktop,
                        $"TOR-AUDIT-LOG-{DateTime.Now:yyyyMMdd-HHmmss}.csv");

                var path =
                    await _audit.ExportCsvAsync(
                        target);

                await _audit.WriteAsync(
                    _currentUser.Username,
                    "AUDIT_EXPORT",
                    "AUDIT_LOG",
                    "",
                    path);

                StatusText.Text =
                    $"Audit-Log exportiert: {path}";
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "Audit-Export fehlgeschlagen: " +
                    ex.Message;
            }
        };

        dsCheck.Click += async (_,_) =>
        {
            try
            {
                var to =
                    DateTimeOffset.Now;

                var from =
                    new DateTimeOffset(
                        to.Year,
                        1,
                        1,
                        0,
                        0,
                        0,
                        to.Offset);

                var report =
                    await _dsfinvkExport.ValidateAsync(
                        from,
                        to);

                var text =
                    report.Ready
                        ? "DSFinV-K 2.4 Preflight: bereit."
                        : "DSFinV-K 2.4 weiterhin gesperrt: " +
                          string.Join(
                              " | ",
                              report.Issues
                                  .Where(x => x.Blocking)
                                  .Select(x => x.Message));

                StatusText.Text = text;
            }
            catch (Exception ex)
            {
                StatusText.Text =
                    "DSFinV-K Prüfung fehlgeschlagen: " +
                    ex.Message;
            }
        };

        exportButtons.Children.Add(auditExport);
        exportButtons.Children.Add(dsCheck);
        exportCenter.Children.Add(exportButtons);
        exportCenter.Children.Add(ReadOnlyRow(
            "TSE TAR",
            "TSE-Aktivierung → TSE TAR EXPORT. BMF verlangt das TAR-Format für TSE-Daten bei Prüfung."));
        exportCenter.Children.Add(ReadOnlyRow(
            "DSFinV-K",
            "TOR erzeugt keinen unvollständigen Prüfdatensatz. Export bleibt bis vollständigem Z-/TSE-Datenmodell und offiziellem Descriptor gesperrt."));
        page.Children.Add(exportCenter);

        page.Children.Add(InfoCard(
            "Wichtig",
            "Solange hier TESTBETRIEB angezeigt wird, darf TOR POS nicht als produktive finanzamtkonforme Kasse eingesetzt oder entsprechend beworben werden. Die Freigabe ist absichtlich nicht manuell überschreibbar.",
            AppTheme.WarningAmberBg));

        return page;
    }



    private Control LicensePage()
    {
        var edition = InstallationEdition.ReadLocked() ?? "KIOSK";
        var page = Page(
            "Lizenzierung",
            "Kunden-Nr. und genau einem PC zugeordnete, kryptografisch signierte TOR-POS-Lizenz. Die kommerzielle Lizenz ersetzt keine TSE-, KassenSichV- oder DSFinV-K-Prüfung.");

        var status = Section("Lizenzstatus");
        _licenseState.FontSize = 19;
        _licenseState.FontWeight = FontWeight.Bold;
        status.Children.Add(LabeledStatusRow("Status", _licenseState));
        status.Children.Add(ReadOnlyRow("Installations-ID", _commercialLicense.InstallationId));
        status.Children.Add(ReadOnlyRow("PC-Gerätecode", _commercialLicense.DeviceCode));
        status.Children.Add(ReadOnlyRow("Lizenzierte Version", edition));
        status.Children.Add(LabeledStatusRow("Kunden-Nr.", _licenseCustomerNumber));
        status.Children.Add(LabeledStatusRow("Kunde", _licenseCustomer));
        status.Children.Add(LabeledStatusRow("Gültigkeit", _licenseValidity));
        page.Children.Add(status);

        var activation = Section("Aktivierung");
        var customerNumber = new TextBox
        {
            PlaceholderText = "z. B. TOR-KD-000123",
            MinHeight = 40,
            MaxLength = 40
        };
        Form(
            activation,
            "Kunden-Nr.",
            customerNumber,
            "Pflichtfeld. Diese Nummer wird vom TOR-Händler vergeben und in der signierten Lizenz gespeichert.");

        var customerName = new TextBox
        {
            PlaceholderText = "Kundenname / Firma",
            MinHeight = 40
        };
        Form(
            activation,
            "Kunde",
            customerName,
            "Wird zusammen mit Kunden-Nr., PC-Gerätecode, Installations-ID und Version in die Aktivierungsanfrage geschrieben.");

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10
        };

        var exportRequest = new Button
        {
            Content = "AKTIVIERUNGSANFRAGE ERSTELLEN",
            MinHeight = 48,
            MinWidth = 250
        };

        var importLicense = new Button
        {
            Content = "LIZENZDATEI IMPORTIEREN",
            MinHeight = 48,
            MinWidth = 220
        };

        var deactivateLicense = new Button
        {
            Content = "LIZENZ DEAKTIVIEREN",
            MinHeight = 48,
            MinWidth = 220,
            Background = new SolidColorBrush(Color.Parse("#6E2730")),
            Foreground = Brushes.White,
            FontWeight = FontWeight.Bold
        };

        exportRequest.Click += (_,_) =>
        {
            try
            {
                var desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                var target = Path.Combine(
                    desktop,
                    $"TOR-Aktivierungsanfrage-{DateTime.Now:yyyyMMdd-HHmmss}.json");

                _commercialLicense.ExportActivationRequest(
                    target,
                    edition,
                    customerNumber.Text ?? "",
                    customerName.Text ?? "",
                    TorRelease.Version);

                StatusText.Text = $"Aktivierungsanfrage gespeichert: {target}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "Aktivierungsanfrage fehlgeschlagen: " + ex.Message;
            }
        };

        importLicense.Click += async (_,_) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(
                new FilePickerOpenOptions
                {
                    Title = "Signierte TOR-POS-Lizenz auswählen",
                    AllowMultiple = false,
                    FileTypeFilter =
                    [
                        new FilePickerFileType("TOR POS Lizenz")
                        {
                            Patterns = ["*.json"]
                        }
                    ]
                });

            var file = files.FirstOrDefault();
            if (file is null)
                return;

            var result = _commercialLicense.Import(
                file.Path.LocalPath,
                edition);

            if (result.IsActive)
            {
                await _audit.WriteAsync(
                    _currentUser.Username,
                    "COMMERCIAL_LICENSE_IMPORT",
                    "LICENSE",
                    result.LicenseId,
                    $"KundenNr={result.CustomerNumber}; Kunde={result.CustomerName}; Device={_commercialLicense.DeviceCode}; Edition={edition}; GueltigBis={result.ValidUntilUtc:O}");
            }

            RefreshLicenseStatus(edition);
            StatusText.Text = result.Message;
        };


        deactivateLicense.Click += async (_,_) =>
        {
            var current = _commercialLicense.Check(edition);
            if (!current.IsActive)
            {
                StatusText.Text = "Keine aktive Lizenz vorhanden, die deaktiviert werden kann.";
                RefreshLicenseStatus(edition);
                return;
            }

            var confirmed = await new LicenseDeactivateConfirmWindow(
                    current.LicenseId,
                    current.CustomerNumber)
                .ShowDialog<bool>(this);

            if (!confirmed)
                return;

            try
            {
                var desktop = Environment.GetFolderPath(
                    Environment.SpecialFolder.DesktopDirectory);
                var receipt = Path.Combine(
                    desktop,
                    $"TOR-Lizenz-Deaktivierung-{current.CustomerNumber}-{DateTime.Now:yyyyMMdd-HHmmss}.json");

                var result = _commercialLicense.Deactivate(
                    edition,
                    _currentUser.Username,
                    receipt);

                await _audit.WriteAsync(
                    _currentUser.Username,
                    "COMMERCIAL_LICENSE_DEACTIVATE",
                    "LICENSE",
                    current.LicenseId,
                    $"KundenNr={current.CustomerNumber}; Kunde={current.CustomerName}; Device={_commercialLicense.DeviceCode}; Edition={edition}; Receipt={receipt}; Result={result.State}");

                RefreshLicenseStatus(edition);
                StatusText.Text = result.State == CommercialLicenseState.Deactivated
                    ? $"Lizenz deaktiviert · Deaktivierungsbeleg: {receipt}"
                    : result.Message;
            }
            catch (Exception ex)
            {
                StatusText.Text = "Lizenz-Deaktivierung fehlgeschlagen: " + ex.Message;
            }
        };

        buttons.Children.Add(exportRequest);
        buttons.Children.Add(importLicense);
        buttons.Children.Add(deactivateLicense);
        activation.Children.Add(buttons);
        page.Children.Add(activation);

        page.Children.Add(InfoCard(
            "Lizenz-Deaktivierung",
            "Die lokale Deaktivierung sperrt die installierte Lizenz-ID auf diesem PC und erzeugt zusätzlich einen Deaktivierungsbeleg auf dem Desktop. Eine neue Aktivierung benötigt danach eine neu ausgestellte Lizenz mit neuer Lizenz-ID.",
            new SolidColorBrush(Color.Parse("#3D2027"))));

        page.Children.Add(InfoCard(
            "Wichtige Trennung",
            "Eine aktive TOR-POS-Lizenz erlaubt die vertragliche Softwarenutzung. Der steuerliche Produktivbetrieb bleibt weiterhin gesperrt, bis reale TSE-, Beleg- und DSFinV-K-Abnahmetests erfolgreich abgeschlossen sind.",
            AppTheme.WarningAmberBg));

        RefreshLicenseStatus(edition);
        return page;
    }

    private void RefreshLicenseStatus(string edition)
    {
        var result = _commercialLicense.Check(edition);
        _licenseState.Text = result.State.ToString().ToUpperInvariant();
        _licenseState.Foreground = result.IsActive ? AppTheme.AccentTeal : AppTheme.WarningAmber;
        _licenseCustomerNumber.Text = string.IsNullOrWhiteSpace(result.CustomerNumber)
            ? "–"
            : result.CustomerNumber;
        _licenseCustomer.Text = string.IsNullOrWhiteSpace(result.CustomerName)
            ? "–"
            : result.CustomerName;
        _licenseValidity.Text = result.ValidUntilUtc is null
            ? result.Message
            : string.IsNullOrWhiteSpace(result.RenewalNotice)
                ? $"{result.ValidUntilUtc:dd.MM.yyyy} · {result.Message}"
                : $"{result.ValidUntilUtc:dd.MM.yyyy} · {result.RenewalNotice} · Bitte rechtzeitig verlängern.";
        if (result.IsExpiringSoon)
            _licenseValidity.Foreground = (result.RemainingDays ?? 30) <= 3 ? new SolidColorBrush(Color.Parse("#FF8A80")) : AppTheme.WarningAmber;
        else
            _licenseValidity.Foreground = Brushes.White;
    }

    private Control SoftwareUpdatePage()
    {
        var page = Page("Software & Update",
            "Version, Lizenzlaufzeit und Updates ohne technische Server-Einstellungen prüfen.");

        var installed = Section("Installierte TOR-POS-Version");
        installed.Children.Add(ReadOnlyRow("Version", $"{TorRelease.Product} {TorRelease.Version}"));
        installed.Children.Add(ReadOnlyRow("Revision", TorRelease.Revision));
        installed.Children.Add(ReadOnlyRow("Edition", InstallationEdition.ReadLocked() ?? "NICHT FESTGELEGT"));
        page.Children.Add(installed);

        var edition = InstallationEdition.ReadLocked() ?? "KIOSK";
        var license = _commercialLicense.Check(edition);
        var licenseSection = Section("Lizenz");
        licenseSection.Children.Add(ReadOnlyRow("Status", license.State.ToString().ToUpperInvariant()));
        licenseSection.Children.Add(ReadOnlyRow("Gültig bis", license.ValidUntilUtc is null ? "–" : license.ValidUntilUtc.Value.ToLocalTime().ToString("dd.MM.yyyy")));
        licenseSection.Children.Add(ReadOnlyRow("Hinweis", string.IsNullOrWhiteSpace(license.RenewalNotice) ? license.Message : license.RenewalNotice));
        page.Children.Add(licenseSection);

        var update = Section("TOR Update");
        var updateEnabled = new CheckBox
        {
            Content = "Automatisch alle 6 Stunden nach Updates suchen",
            MinHeight = 40
        };
        var updateStatus = new TextBlock
        {
            Text = "Update-Status wird geladen …",
            TextWrapping = TextWrapping.Wrap
        };
        var updateSave = new Button { Content = "AUTOMATISCHE PRÜFUNG SPEICHERN", MinHeight = 44 };
        var updateCheck = new Button { Content = "JETZT NACH UPDATE SUCHEN", MinHeight = 48 };
        updateSave.Click += async (_,_) =>
        {
            try
            {
                var updater = new TorUpdateService(_settings, _backup);
                await updater.SetEnabledAsync(updateEnabled.IsChecked == true);
                updateStatus.Text = updateEnabled.IsChecked == true
                    ? "Automatische Update-Prüfung ist aktiv."
                    : "Automatische Update-Prüfung ist deaktiviert. Manuelle Prüfung bleibt möglich.";
            }
            catch (Exception ex) { updateStatus.Text = "Update-Einstellung: " + ex.Message; }
        };
        updateCheck.Click += async (_,_) =>
        {
            updateCheck.IsEnabled = false;
            try
            {
                var updater = new TorUpdateService(_settings, _backup);
                var result = await updater.CheckAsync(edition, force: true);
                updateStatus.Text = result.UpdateAvailable && result.Manifest is not null
                    ? $"Neue Version verfügbar: {result.Manifest.Revision} · {result.Manifest.Version}" +
                      (result.Manifest.Mandatory ? " · WICHTIGES UPDATE" : "") +
                      (string.IsNullOrWhiteSpace(result.Manifest.ReleaseNotes) ? "" : "\n" + result.Manifest.ReleaseNotes) +
                      "\nZur Kasse zurückkehren: oben erscheint für Admin der UPDATE-Button. Installation startet erst nach Sicherheitsprüfung und Backup."
                    : result.Message;
            }
            catch (Exception ex) { updateStatus.Text = "Update-Prüfung: " + ex.Message; }
            finally { updateCheck.IsEnabled = true; }
        };
        update.Children.Add(updateEnabled);
        update.Children.Add(updateSave);
        update.Children.Add(updateCheck);
        update.Children.Add(updateStatus);
        update.AttachedToVisualTree += async (_,_) =>
        {
            try
            {
                var updater = new TorUpdateService(_settings, _backup);
                updateEnabled.IsChecked = await updater.EnabledAsync();
                var last = await _settings.GetAsync("update.last_check_utc", "");
                var state = await _settings.GetAsync("update.last_status", "noch nicht geprüft");
                updateStatus.Text = string.IsNullOrWhiteSpace(last)
                    ? "Noch keine Update-Prüfung."
                    : $"Letzte Prüfung: {last} · {state}";
            }
            catch (Exception ex) { updateStatus.Text = "Update-Status: " + ex.Message; }
        };
        page.Children.Add(update);

        page.Children.Add(InfoCard(
            "Sicher aktualisieren",
            "TOR POS installiert niemals mitten in einem Verkauf oder einer ungeklärten Zahlung. Vor der Installation werden Setup-Prüfsumme/Signatur geprüft und eine Datenbanksicherung erstellt. Die technische Update-Server-Adresse bleibt im geschützten Technikerbereich.",
            AppTheme.InfoCardBg));
        return page;
    }

    private Control SystemPage()
    {
        var page = Page("System",
            "Technische Informationen für Installation, Service und Diagnose.");

        var section = Section("Installation");
        section.Children.Add(ReadOnlyRow("Version", $"{TorRelease.Product} {TorRelease.Version} · {TorRelease.Revision}"));
        section.Children.Add(ReadOnlyRow("Datenordner", AppPaths.DataDirectory));
        section.Children.Add(ReadOnlyRow("Datenbank", AppPaths.DatabasePath));
        section.Children.Add(ReadOnlyRow(
            "Schema-Migration",
            $"Versioniert ab R69 · Zielversion {SchemaMigrationService.TargetSchemaVersion} · Status im Diagnose-Fenster"));
        section.Children.Add(ReadOnlyRow("Produktbilder", AppPaths.ProductImagesPath));
        section.Children.Add(ReadOnlyRow("Backups", AppPaths.BackupsPath));
        page.Children.Add(section);

        var update = Section("TOR Update · Technische Quelle");
        var updateServer = new TextBox { MinHeight = 40, PlaceholderText = "Leer = TOR Cloud Server verwenden" };
        var updateStatus = new TextBlock { Text = "Update-Server wird geladen …", TextWrapping = TextWrapping.Wrap };
        Form(update, "Update-Server", updateServer,
            "Nur für Installation/Service. Produktiv ausschließlich HTTPS; localhost darf für Entwicklung HTTP verwenden.");
        var updateSave = new Button { Content = "UPDATE-SERVER SPEICHERN", MinHeight = 44 };
        updateSave.Click += async (_,_) =>
        {
            try
            {
                var updater = new TorUpdateService(_settings, _backup);
                await updater.SetServerUrlAsync(updateServer.Text ?? "");
                updateStatus.Text = string.IsNullOrWhiteSpace(updateServer.Text)
                    ? "Gespeichert: TOR Cloud Server wird als Update-Quelle verwendet."
                    : "Technische Update-Quelle gespeichert.";
            }
            catch (Exception ex) { updateStatus.Text = "Update-Server: " + ex.Message; }
        };
        update.Children.Add(updateSave);
        update.Children.Add(updateStatus);
        update.AttachedToVisualTree += async (_,_) =>
        {
            try
            {
                updateServer.Text = await _settings.GetAsync("update.server_url", "");
                updateStatus.Text = string.IsNullOrWhiteSpace(updateServer.Text)
                    ? "Keine separate Update-Quelle: TOR Cloud Server wird verwendet."
                    : "Separate technische Update-Quelle ist konfiguriert.";
            }
            catch (Exception ex) { updateStatus.Text = "Update-Server: " + ex.Message; }
        };
        page.Children.Add(update);

        var performance = Section("Performance & Diagnose");
        performance.Children.Add(ReadOnlyRow(
            "Bewertung",
            $"Ab {PerformanceCounters.SlowThresholdMs:0} ms wird eine Messung als langsam markiert. Bediener-Wartezeit in Dialogen wird nicht als Systemlatenz gewertet."));

        var snapshots = _performance.SnapshotAll();
        if (snapshots.Count == 0)
        {
            performance.Children.Add(ToggleRow(new TextBlock { Text = "Noch keine Messwerte vorhanden.", Opacity = 0.65 }));
        }
        else
        {
            foreach (var item in snapshots.OrderByDescending(x => x.Value.MaxMs))
            {
                performance.Children.Add(ReadOnlyRow(
                    item.Key,
                    $"Letzt {item.Value.LastMs:0.0} ms · Ø {item.Value.AverageMs:0.0} ms · Max {item.Value.MaxMs:0.0} ms · " +
                    $"{item.Value.Count} Messungen · langsam {item.Value.SlowCount}"));
            }
        }

        var diagnostics = new Button
        {
            Content = "SYSTEMSTATUS / DIAGNOSE ÖFFNEN",
            MinHeight = 48,
            MinWidth = 260
        };
        diagnostics.Click += async (_,_) =>
        {
            await _windowFactory
                .CreateDiagnosticsWindow()
                .ShowDialog(this);
        };
        performance.Children.Add(diagnostics);
        page.Children.Add(performance);

        page.Children.Add(InfoCard(
            "Fiskalstatus",
            "Swissbit-Bridge, TSE-Ausfallbehandlung und ZVT-Anbindung sind vorbereitet. Der vollständige DSFinV-K-Export und die Realhardware-Abnahme sind noch nicht freigegeben; TOR POS bleibt deshalb im TESTBETRIEB.",
            AppTheme.WarningAmberBg));

        return page;
    }

    private async Task LoadAsync()
    {
        var values = await _settings.LoadAllAsync();

        foreach (var pair in _text)
        {
            var value = values.GetText(pair.Key);
            if (pair.Key is "function.storno_reasons"
                or "function.bon_storno_reasons"
                or "function.discount_reasons"
                or "function.cancel_reasons")
                value = value.Replace("|", Environment.NewLine);
            pair.Value.Text = value;
        }

        foreach (var pair in _check)
            pair.Value.IsChecked = values.GetBool(
                pair.Key,
                fallback: pair.Key is "printer.auto_cut.enabled" or "printer.drawer_kick.enabled");

        _reportSmtpPasswordProtected = values.GetText("reports.email.smtp.password_protected");
        _reportSmtpPassword.Text = "";
        if (string.IsNullOrWhiteSpace(_reportSmtpPasswordProtected))
            _reportSmtpPassword.PlaceholderText = "Google App-Passwort eingeben";
        else if (TorSecretProtector.TryUnprotect(_reportSmtpPasswordProtected, out _))
            _reportSmtpPassword.PlaceholderText = "Gespeichertes App-Passwort vorhanden · leer lassen zum Behalten";
        else
            _reportSmtpPassword.PlaceholderText = "Gespeichertes Passwort nicht lesbar · neu eingeben";

        await RefreshGoogleMailStatusAsync();

        foreach (var pair in _combo)
        {
            var value = values.GetText(pair.Key);
            if (!string.IsNullOrWhiteSpace(value))
                pair.Value.SelectedItem = value;
        }

        UpdateTouchLayoutSummary();
        StatusText.Text = "Einstellungen geladen";
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            var values = new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);

            foreach (var pair in _text)
            {
                if (pair.Key is "backup.daily.last_success" or "backup.daily.last_error" or "backup.daily.last_path"
                    or "reports.email.monthly.last_period" or "reports.email.monthly.last_success"
                    or "reports.email.monthly.last_error" or "reports.email.monthly.last_folder")
                    continue;
                var value = pair.Value.Text ?? "";
                if (pair.Key is "function.storno_reasons"
                    or "function.bon_storno_reasons"
                    or "function.discount_reasons"
                    or "function.cancel_reasons")
                    value = string.Join("|", value.Split(['\r','\n'], StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()));
                values[pair.Key] = value;
            }

            foreach (var pair in _check)
                values[pair.Key] = (pair.Value.IsChecked == true).ToString().ToLowerInvariant();

            foreach (var pair in _combo)
                values[pair.Key] = pair.Value.SelectedItem?.ToString() ?? "";

            NormalizeIntSetting(values, "ui.product.columns", 4, 2, 8);
            NormalizeIntSetting(values, "ui.product.rows", 10, 2, 12);
            NormalizeIntSetting(values, "ui.category.columns", 4, 2, 8);
            NormalizeIntSetting(values, "ui.category.rows", 6, 1, 10);
            NormalizeIntSetting(values, "ui.touch.font_size", 18, 11, 28);
            NormalizeIntSetting(values, "order_display.refresh_seconds", 2, 1, 10);
            NormalizeTimeSetting(values, "backup.daily.time", "00:00");
            NormalizeIntSetting(values, "reports.email.monthly.day", 1, 1, 28);
            NormalizeTimeSetting(values, "reports.email.monthly.time", "00:15");
            NormalizeIntSetting(values, "reports.email.smtp.port", 587, 1, 65535);

            var enteredSmtpPassword = (_reportSmtpPassword.Text ?? "").Trim();
            if (ReportEmailService.LooksMaskedPassword(enteredSmtpPassword))
                throw new InvalidOperationException("Maskierte Zeichen sind kein SMTP App-Passwort. Bitte das echte App-Passwort eingeben oder das Feld leer lassen.");

            var monthlyEmailEnabled = values.GetValueOrDefault("reports.email.monthly.enabled", "false") == "true";
            var activeTransport = (await _settings.GetAsync("reports.email.transport", "smtp")).Trim().ToLowerInvariant();
            string smtpPasswordForValidation;
            if (!string.IsNullOrWhiteSpace(enteredSmtpPassword))
            {
                smtpPasswordForValidation = ReportEmailService.NormalizeAppPassword(enteredSmtpPassword);
                _reportSmtpPasswordProtected = TorSecretProtector.Protect(smtpPasswordForValidation);
            }
            else if (monthlyEmailEnabled && activeTransport != "google" && !string.IsNullOrWhiteSpace(_reportSmtpPasswordProtected))
            {
                smtpPasswordForValidation = ReportEmailService.ResolveAppPassword("", _reportSmtpPasswordProtected);
            }
            else
            {
                smtpPasswordForValidation = "";
            }
            values["reports.email.smtp.password_protected"] = _reportSmtpPasswordProtected;

            if (monthlyEmailEnabled)
            {
                if (activeTransport == "google")
                {
                    var googleConnectionId = (await _settings.GetAsync("reports.email.google.connection_id", "")).Trim();
                    if (string.IsNullOrWhiteSpace(googleConnectionId))
                        throw new InvalidOperationException("Monatlicher Versand ist auf Google gestellt, aber kein Google-Konto ist verbunden.");
                    try { _ = new System.Net.Mail.MailAddress(values.GetValueOrDefault("reports.email.recipient", "")); }
                    catch { throw new InvalidOperationException("Gültige Empfänger-E-Mail fehlt."); }
                }
                else
                {
                    ReportEmailService.Validate(new ReportEmailService.MailConfig(
                        values.GetValueOrDefault("reports.email.recipient", ""),
                        values.GetValueOrDefault("reports.email.sender", ""),
                        values.GetValueOrDefault("reports.email.smtp.host", ""),
                        int.Parse(values.GetValueOrDefault("reports.email.smtp.port", "587")),
                        values.GetValueOrDefault("reports.email.smtp.ssl", "true") != "false",
                        values.GetValueOrDefault("reports.email.smtp.user", ""),
                        smtpPasswordForValidation));
                }
            }

            // R132 (DSFinV-K 3.2): a change of the company master data while
            // Vorgänge are waiting for a closing creates that closing first.
            var database = new SqliteDatabase(AppPaths.DatabasePath);
            var masterData = new DsfinvkMasterDataService(
                database,
                _settings,
                new BusinessManagementService(database, _settings, _audit),
                new DailyClosingGuard(new ParkedReceiptRepository(database), database),
                _audit);
            var saved = await masterData.SaveSettingsAsync(values, _currentUser.Username);
            _reportSmtpPassword.Text = "";
            _reportSmtpPassword.PlaceholderText = string.IsNullOrWhiteSpace(_reportSmtpPasswordProtected)
                ? "Google App-Passwort eingeben"
                : "Gespeichertes App-Passwort vorhanden · leer lassen zum Behalten";
            TouchKeyboard.AutoOpen = values.GetValueOrDefault("ui.keyboard.auto", "true") != "false";
            if(values.TryGetValue("ui.language",out var uiLanguage)) { UiLanguage.Set(uiLanguage); UiLanguage.Apply(this); }
            await _audit.WriteAsync(
                _currentUser.Username,
                "SETTINGS_SAVE",
                "SETTINGS",
                "",
                "Einstellungen gespeichert; keine Zugangsdaten protokolliert.");
            await RefreshLegalStatusAsync();
            StatusText.Text = saved.AutomaticClosing is { } closing
                ? $"Alle Einstellungen gespeichert · vorher automatisch Kassenabschluss Z {closing.ZNumber:000000} erstellt (Stammdatenänderung, DSFinV-K 3.2)"
                : "Alle Einstellungen gespeichert";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Speichern fehlgeschlagen: {ex.Message}";
        }
    }

    private static void NormalizeIntSetting(
        IDictionary<string,string> values,
        string key,
        int fallback,
        int min,
        int max)
    {
        if (!values.TryGetValue(key, out var raw) ||
            !int.TryParse(raw, out var parsed))
        {
            parsed = fallback;
        }

        values[key] = Math.Clamp(parsed, min, max).ToString();
    }

    private static void NormalizeTimeSetting(IDictionary<string,string> values, string key, string fallback)
    {
        if (!values.TryGetValue(key, out var raw)) { values[key] = fallback; return; }
        var parts = raw.Trim().Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var hour) || !int.TryParse(parts[1], out var minute) || hour is < 0 or > 23 || minute is < 0 or > 59)
        {
            values[key] = fallback;
            return;
        }
        values[key] = $"{hour:00}:{minute:00}";
    }

    private void UpdateTouchLayoutSummary()
    {
        int Read(string key, int fallback, int min, int max)
        {
            if (_text.TryGetValue(key, out var box) &&
                int.TryParse(box.Text, out var parsed))
                return Math.Clamp(parsed, min, max);

            return fallback;
        }

        var productColumns = Read("ui.product.columns", 4, 2, 8);
        var productRows = Read("ui.product.rows", 10, 2, 12);
        var categoryColumns = Read("ui.category.columns", 4, 2, 8);
        var categoryRows = Read("ui.category.rows", 6, 1, 10);

        _touchLayoutSummary.Text =
            $"Hauptkasse: {categoryColumns * categoryRows} Warengruppen gleichzeitig " +
            $"({categoryColumns} × {categoryRows}) · " +
            $"{productColumns * productRows} Artikel je Warengruppe/Seite " +
            $"({productColumns} × {productRows}). " +
            "Sind mehr Einträge vorhanden, erscheinen automatisch ◀ / ▶ Seitentasten.";
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close(true);

    private async void OnNavigationChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not string name || !_pages.TryGetValue(name, out var page))
            return;

        if (string.Equals(name, "Erweitert / Techniker 🔒", StringComparison.OrdinalIgnoreCase) && !_technicianUnlocked)
        {
            if (DateTimeOffset.UtcNow < _technicianLockedUntil)
            {
                var mins = Math.Max(1, (int)Math.Ceiling((_technicianLockedUntil - DateTimeOffset.UtcNow).TotalMinutes));
                await ShowSimpleMessageAsync("Techniker gesperrt", $"Zu viele Fehlversuche. Bitte in {mins} Min. erneut versuchen.");
                NavList.SelectedIndex = _lastNavigationIndex;
                return;
            }

            var ok = await EnsureTechnicianAccessAsync();
            if (!ok)
            {
                _technicianFailedAttempts++;
                if (_technicianFailedAttempts >= 5)
                {
                    _technicianFailedAttempts = 0;
                    _technicianLockedUntil = DateTimeOffset.UtcNow.AddMinutes(5);
                    await SaveTechnicianGuardAsync();
                    await ShowSimpleMessageAsync("Techniker gesperrt", "5 Fehlversuche. Der Technikerbereich ist für 5 Minuten gesperrt.");
                }
                else
                {
                    await SaveTechnicianGuardAsync();
                }
                NavList.SelectedIndex = _lastNavigationIndex;
                return;
            }

            _technicianFailedAttempts = 0;
            _technicianLockedUntil = DateTimeOffset.MinValue;
            _technicianUnlocked = true;
            await SaveTechnicianGuardAsync();
        }

        _lastNavigationIndex = NavList.SelectedIndex;
        SettingsContent.Content = page;
    }

    private async Task LoadTechnicianGuardAsync()
    {
        var values = await _settings.LoadAllAsync();
        if (values.TryGetValue("security.technician.failed_attempts", out var failed) && int.TryParse(failed, out var count))
            _technicianFailedAttempts = Math.Clamp(count, 0, 4);
        if (values.TryGetValue("security.technician.locked_until_utc", out var locked) &&
            DateTimeOffset.TryParse(locked, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind, out var until))
            _technicianLockedUntil = until;
        if (_technicianLockedUntil <= DateTimeOffset.UtcNow && _technicianLockedUntil != DateTimeOffset.MinValue)
        {
            _technicianLockedUntil = DateTimeOffset.MinValue;
            _technicianFailedAttempts = 0;
            await SaveTechnicianGuardAsync();
        }
    }

    private Task SaveTechnicianGuardAsync()
    {
        return _settings.SaveManyAsync(new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase)
        {
            ["security.technician.failed_attempts"] = _technicianFailedAttempts.ToString(),
            ["security.technician.locked_until_utc"] = _technicianLockedUntil == DateTimeOffset.MinValue
                ? ""
                : _technicianLockedUntil.ToUniversalTime().ToString("O")
        });
    }

    // R75.2 security hotfix: the technician gate used to compare against one fixed
    // SHA-256 digest baked into every shipped copy of the application (candidate "4909").
    // That is a single global secret recoverable offline from the binary in seconds and
    // identical on every customer installation, defeating the point of a service-only
    // gate in front of TSE/fiscal/payment/licensing settings. It is now a normal
    // per-installation secret stored the same way as user credentials
    // (PBKDF2-SHA256/600k via AuthenticationService), configured on first use and
    // changeable afterwards from the System page.
    private const string TechnicianHashKey = "security.technician.password_hash";
    private const string TechnicianSaltKey = "security.technician.password_salt";
    private const string TechnicianKdfKey = "security.technician.password_kdf";
    private const string TechnicianIterationsKey = "security.technician.password_iterations";

    private async Task<bool> EnsureTechnicianAccessAsync()
    {
        var values = await _settings.LoadAllAsync();
        var hash = values.GetValueOrDefault(TechnicianHashKey, "");
        var salt = values.GetValueOrDefault(TechnicianSaltKey, "");

        if (string.IsNullOrEmpty(hash) || string.IsNullOrEmpty(salt))
            return await SetupTechnicianPasswordAsync(isFirstSetup: true);

        var kdf = values.GetValueOrDefault(TechnicianKdfKey, AuthenticationService.CurrentKdfAlgorithm);
        if (!int.TryParse(values.GetValueOrDefault(TechnicianIterationsKey, "0"), out var iterations))
            iterations = 0;

        var candidate = await AskTechnicianPasswordAsync();
        if (candidate is null)
            return false;

        return AuthenticationService.VerifyTechnicianSecret(candidate, salt, hash, kdf, iterations);
    }

    private async Task<string?> AskTechnicianPasswordAsync()
    {
        var input = new TextBox { PasswordChar = '●', FontSize = 24, MinWidth = 260 };
        var ok = new Button { Content = "ÖFFNEN", MinHeight = 46 };
        var cancel = new Button { Content = "ABBRECHEN", MinHeight = 46 };
        var dialog = new Window
        {
            Title = "Techniker-Zugang",
            Width = 420,
            Height = 235,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ok.Click += (_, _) => dialog.Close(input.Text ?? "");
        cancel.Click += (_, _) => dialog.Close(null);
        input.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; dialog.Close(input.Text ?? ""); } };
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "Erweitert / Techniker", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock { Text = "Techniker-Passwort eingeben.", Opacity = 0.75 },
                input,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    ColumnSpacing = 10,
                    Children = { ok, cancel }
                }
            }
        };
        Grid.SetColumn(cancel, 1);
        dialog.Opened += (_, _) => input.Focus();
        return await dialog.ShowDialog<string?>(this);
    }

    private async Task<bool> SetupTechnicianPasswordAsync(bool isFirstSetup)
    {
        var input = new TextBox { PasswordChar = '●', FontSize = 22, MinWidth = 280 };
        var confirm = new TextBox { PasswordChar = '●', FontSize = 22, MinWidth = 280 };
        var status = new TextBlock { Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var save = new Button { Content = "SPEICHERN", MinHeight = 46 };
        var cancel = new Button { Content = "ABBRECHEN", MinHeight = 46 };
        var dialog = new Window
        {
            Title = "Techniker-Passwort einrichten",
            Width = 460,
            Height = 360,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };

        void TrySave()
        {
            var value = input.Text ?? "";
            if (value.Length < 6)
            {
                status.Text = "Mindestens 6 Zeichen.";
                return;
            }
            if (!string.Equals(value, confirm.Text ?? "", StringComparison.Ordinal))
            {
                status.Text = "Die beiden Eingaben stimmen nicht überein.";
                return;
            }
            dialog.Close(value);
        }

        save.Click += (_, _) => TrySave();
        cancel.Click += (_, _) => dialog.Close(null);
        confirm.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { e.Handled = true; TrySave(); } };

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Techniker-Passwort", FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = isFirstSetup
                        ? "Für diese Installation ist noch kein Techniker-Passwort vergeben. Dieses Passwort schützt Kartenzahlung/Terminal, TSE-Aktivierung, Recht & Fiskal, Lizenzierung und System und sollte nur der zuständigen Technik/dem Fachhändler bekannt sein, nicht zwingend dem Kassenbetreiber."
                        : "Neues Techniker-Passwort festlegen.",
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.85
                },
                new TextBlock { Text = "Neues Passwort (mind. 6 Zeichen)" },
                input,
                new TextBlock { Text = "Wiederholen" },
                confirm,
                status,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,*"),
                    ColumnSpacing = 10,
                    Children = { save, cancel }
                }
            }
        };
        Grid.SetColumn(cancel, 1);
        dialog.Opened += (_, _) => input.Focus();

        var newPassword = await dialog.ShowDialog<string?>(this);
        if (newPassword is null)
            return false;

        var (salt, hash) = AuthenticationService.HashTechnicianSecret(newPassword);
        await _settings.SaveManyAsync(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [TechnicianHashKey] = hash,
            [TechnicianSaltKey] = salt,
            [TechnicianKdfKey] = AuthenticationService.CurrentKdfAlgorithm,
            [TechnicianIterationsKey] = AuthenticationService.CurrentPbkdf2Iterations.ToString()
        });

        await _audit.WriteAsync(
            _currentUser.Username,
            isFirstSetup ? "TECHNICIAN_PASSWORD_SETUP" : "TECHNICIAN_PASSWORD_CHANGED",
            "SETTINGS",
            "",
            isFirstSetup ? "Techniker-Passwort erstmalig eingerichtet." : "Techniker-Passwort geändert.");

        return true;
    }

    private async Task RefreshGoogleMailStatusAsync()
    {
        try
        {
            if (App.CloudSync is not { } cloud)
            {
                _googleMailStatus.Text = "Google: TOR POS Cloud-Dienst nicht verfügbar.";
                return;
            }
            using var gmail = new GoogleGmailService(_settings, cloud);
            var state = await gmail.GetConnectionAsync();
            var transport = (await _settings.GetAsync("reports.email.transport", "smtp")).Trim().ToLowerInvariant();
            if (state.Connected)
            {
                _googleMailStatus.Text = $"Google verbunden ✓  {state.AccountEmail}\nAktiver Versandweg: {(transport == "google" ? "Gmail API / OAuth" : "SMTP-Fallback")}";
            }
            else
            {
                _googleMailStatus.Text = $"Noch kein Google-Konto verbunden.\nAktiver Versandweg: {(transport == "google" ? "Google (erneute Anmeldung erforderlich)" : "SMTP-Fallback")}";
            }
        }
        catch (Exception ex)
        {
            _googleMailStatus.Text = "Google-Status konnte nicht gelesen werden: " + ex.Message;
        }
    }

    private async Task<bool> ConfirmSimpleAsync(string title, string message)
    {
        var yes = new Button { Content = "JA, TRENNEN", MinHeight = 44, FontWeight = FontWeight.SemiBold };
        var cancel = new Button { Content = "ABBRECHEN", MinHeight = 44 };
        var dialog = new Window
        {
            Title = title,
            Width = 470,
            Height = 240,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        yes.Click += (_, _) => dialog.Close(true);
        cancel.Click += (_, _) => dialog.Close(false);
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        buttons.Children.Add(yes); buttons.Children.Add(cancel); Grid.SetColumn(cancel, 1);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task ShowSimpleMessageAsync(string title, string message)
    {
        var close = new Button { Content = "OK", MinHeight = 44 };
        var dialog = new Window
        {
            Title = title,
            Width = 430,
            Height = 210,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        close.Click += (_, _) => dialog.Close();
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(24),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = title, FontSize = 22, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                close
            }
        };
        await dialog.ShowDialog(this);
    }

    private StackPanel Page(string title, string subtitle)
    {
        var panel = new StackPanel { Spacing = 14 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 27, FontWeight = FontWeight.Bold });
        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0,-5,0,4)
        });
        return panel;
    }

    private StackPanel Section(string title)
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0,0,0,6) };
        panel.Children.Add(new Border
        {
            Background = new SolidColorBrush(Color.Parse("#17253A")),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14,10),
            Child = new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold }
        });
        return panel;
    }

    private Control ToggleRow(Control child)
    {
        return new Border
        {
            Background = AppTheme.SurfacePanel,
            BorderBrush = new SolidColorBrush(Color.Parse("#20314A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12,8),
            Child = child
        };
    }

    private Control InfoCard(string title, string text, IBrush background)
    {
        return new Border
        {
            Background = background,
            BorderBrush = AppTheme.PanelBorder,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(AppTheme.CardRadius),
            Padding = new Thickness(15),
            Child = new StackPanel
            {
                Spacing = 5,
                Children =
                {
                    new TextBlock { Text = title, FontWeight = FontWeight.Bold },
                    new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 }
                }
            }
        };
    }

    private TextBox Text(string key, bool multiline = false)
    {
        var box = new TextBox
        {
            MinHeight = 38,
            AcceptsReturn = multiline,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap
        };
        _text[key] = box;
        return box;
    }

    private CheckBox Check(string key, string caption)
    {
        var box = new CheckBox { Content = caption, MinHeight = 30 };
        _check[key] = box;
        return box;
    }

    private ComboBox Combo(string key, params string[] values)
    {
        var box = new ComboBox { ItemsSource = values, MinHeight = 38, MinWidth = 230 };
        if (values.Length > 0) box.SelectedIndex = 0;
        _combo[key] = box;
        return box;
    }

    private void Form(StackPanel section, string label, Control input, string? description = null)
    {
        var container = new Border
        {
            Background = AppTheme.SurfacePanel,
            BorderBrush = new SolidColorBrush(Color.Parse("#20314A")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(12,9)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("230,*"),
            RowDefinitions = description is null ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto"),
            ColumnSpacing = 14
        };

        grid.Children.Add(new TextBlock
        {
            Text = label,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.82
        });

        Grid.SetColumn(input, 1);
        grid.Children.Add(input);

        if (description is not null)
        {
            var help = new TextBlock
            {
                Text = description,
                Opacity = 0.50,
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0,3,0,0)
            };
            Grid.SetColumn(help, 1);
            Grid.SetRow(help, 1);
            grid.Children.Add(help);
        }

        container.Child = grid;
        section.Children.Add(container);
    }

    private Control ReadOnlyRow(string label, string value)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("200,*"), ColumnSpacing = 12 };
        grid.Children.Add(new TextBlock { Text = label, Opacity = 0.55 });
        var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(v,1);
        grid.Children.Add(v);
        return ToggleRow(grid);
    }
}
