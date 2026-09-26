using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using TorPos.Core;
using TorPos.Infrastructure;

namespace TorPos.App;

public sealed class ProductEditorWindow : Window
{
    private readonly IProductRepository _repo;
    private readonly IProductCatalog _catalog;
    private readonly ProductImageStore _images;
    private readonly BusinessManagementService _management;
    private readonly PromotionCampaignService _promotions;
    private readonly AuthenticatedUser _user;
    private readonly string _initialBarcode;

    private readonly TabControl _tabs = new();

    // 1. GRUPPE
    private readonly ListBox _groupList = new();
    private readonly TextBox _groupName = new();
    private readonly TextBox _groupSort = new();

    // 2. WARENGRUPPE
    private readonly ListBox _categoryList = new();
    private readonly ComboBox _categoryGroup = new();
    private readonly TextBox _categoryName = new();
    private readonly ComboBox _categoryVat = new();
    private readonly TextBox _categorySort = new();
    private readonly ComboBox _categoryKitchenStation = new();
    // R97: opt this Warengruppe out of the Im-Haus/Außer-Haus VAT rule
    // (getränke already stay at 19% regardless - ImHausVat.Effective only
    // ever touches 7% - but admins wanted an explicit, visible switch).
    private readonly CheckBox _categoryImHausApplicable = new() { Content = "Im Haus/Außer Haus MwSt.-Regel anwenden", IsChecked = true };
    private readonly TextBox _categoryColor = new() { Text = "#17466A" };
    private readonly Border _categoryColorPreview = new()
    {
        Width = 54,
        Height = 34,
        CornerRadius = new Avalonia.CornerRadius(7),
        Background = new SolidColorBrush(Color.Parse("#17466A")),
        BorderBrush = Brushes.White,
        BorderThickness = new Avalonia.Thickness(1)
    };

    // 3. ARTIKEL
    private readonly ListBox _articleCategoryList = new();
    private readonly ListBox _articleList = new();
    private readonly ComboBox _articleCategory = new();
    private readonly TextBlock _articleVat =
        new()
        {
            Text = "Bitte Warengruppe auswählen",
            FontSize = 17,
            FontWeight = FontWeight.Bold,
            Foreground = AppTheme.AccentTeal
        };

    private readonly TextBox _name = new();
    private readonly TextBox _barcode = new();
    private readonly TextBox _sku = new() { IsReadOnly = true, PlaceholderText = "wird automatisch vergeben" };
    private readonly TextBox _price = new();
    private readonly TextBox _purchasePrice = new();
    private readonly TextBox _pfand = new();
    private readonly CheckBox _soldByWeight = new()
    {
        Content = "Verkauf nach Gewicht (Gramm / Kilogramm) · Preis pro kg",
        FontWeight = FontWeight.SemiBold
    };
    private readonly TextBlock _weightHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = AppTheme.AccentTeal,
        IsVisible = false
    };
    private readonly ComboBox _unit = new();
    private readonly TextBox _stock = new();
    private readonly TextBox _minStock = new();
    private readonly TextBox _articleSearch = new()
    {
        PlaceholderText = "Name, EAN oder Artikel-Nr. · Scanner möglich"
    };
    private readonly TextBlock _imageText = new();
    private readonly Image _imagePreview = new()
    {
        Height = 150,
        Stretch = Stretch.Uniform,
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
    };
    private Bitmap? _imagePreviewBitmap;

    // 4. EXTRAS (nur IMBISS)
    private readonly ListBox _extraList = new();
    private readonly ComboBox _extraCategory = new();
    private readonly TextBox _extraName = new();
    private readonly TextBox _extraPrice = new();
    private readonly TextBox _extraSort = new();
    private readonly TextBlock _extraVat = new();
    private IReadOnlyList<ExtraItem> _extras = Array.Empty<ExtraItem>();

    // Varianten und R49 Menü/Combo werden direkt am Artikel gepflegt.
    private readonly ListBox _variantList = new();
    private readonly ObservableCollection<VariantRow> _variants = new();
    private readonly ListBox _comboList = new();
    private readonly ObservableCollection<ComboRow> _comboItems = new();
    private readonly TextBox _comboChoiceGroup = new()
    {
        PlaceholderText = "optional, z. B. GETRÄNK"
    };
    private readonly TextBlock _comboSummary = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = AppTheme.AccentTeal
    };

    private ProductGroup? _selectedGroup;
    private Category? _selectedCategory;
    private Category? _selectedArticleCategoryForList;
    private Product? _selectedArticle;
    private ExtraItem? _selectedExtra;

    private string _selectedImage = "";
    private bool _saved;
    private bool _syncingArticleCategoryUi;

    public ProductEditorWindow(
        IProductRepository repo,
        IProductCatalog catalog,
        ProductImageStore images,
        BusinessManagementService management,
        PromotionCampaignService promotions,
        AuthenticatedUser user,
        string initialBarcode)
    {
        _repo = repo;
        _catalog = catalog;
        _images = images;
        _management = management;
        _promotions = promotions;
        _user = user;
        _initialBarcode = initialBarcode;

        Title = "TOR POS – Stammdaten";
        Width = 1180;
        Height = 800;
        MinWidth = 1000;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowState = WindowState.Maximized;

        _categoryVat.ItemsSource =
            new[] { "7", "19" };
        _categoryVat.SelectedItem = "19";
        _categoryColor.TextChanged += (_, _) => RefreshCategoryColorPreview();

        _categoryKitchenStation.ItemsSource = KitchenStations.All;
        _categoryKitchenStation.ItemTemplate =
            new FuncDataTemplate<string>((station, _) =>
                new TextBlock { Text = KitchenStations.DisplayName(station ?? "") });
        _categoryKitchenStation.SelectedItem = KitchenStations.None;

        _unit.ItemsSource =
            new[]
            {
                "Stück",
                "kg",
                "g",
                "Liter",
                "Portion"
            };
        _unit.SelectedIndex = 0;
        _soldByWeight.IsCheckedChanged += (_,_) => ApplyWeightMode();

        _variantList.ItemsSource =
            _variants;
        _comboList.ItemsSource = _comboItems;
        _price.TextChanged += (_,_) => RefreshComboSummary();

        _groupList.SelectionChanged +=
            OnGroupSelected;

        _categoryList.SelectionChanged +=
            OnCategorySelected;

        _articleCategoryList.SelectionChanged +=
            OnArticleCategoryFilterSelected;

        _articleList.SelectionChanged +=
            OnArticleSelected;

        _extraList.SelectionChanged +=
            OnExtraSelected;

        _articleCategory.SelectionChanged +=
            (_,_) =>
            {
                if (_syncingArticleCategoryUi)
                {
                    RefreshInheritedVat();
                    return;
                }

                _syncingArticleCategoryUi = true;
                try
                {
                    if (_articleCategory.SelectedItem is Category category)
                    {
                        _selectedArticleCategoryForList = category;
                        var row = (_articleCategoryList.ItemsSource as IEnumerable<CategoryRow>)?
                            .FirstOrDefault(x => x.Id == category.Id);
                        if (row is not null)
                            _articleCategoryList.SelectedItem = row;
                    }
                    RefreshArticleList();
                    RefreshInheritedVat();
                }
                finally
                {
                    _syncingArticleCategoryUi = false;
                }
            };

        _articleSearch.TextChanged += (_,_) => RefreshArticleList();
        _articleSearch.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                SelectExactArticleFromSearch();
                e.Handled = true;
            }
        };

        _extraCategory.SelectionChanged +=
            (_,_) => RefreshExtraVat();

        Content = BuildUi();

        Opened += async (_,_) =>
        {
            await _catalog.ReloadAsync();
            _extras = await _repo.GetExtrasAsync();
            RefreshAllBindings();
            NewGroup();
            NewCategory();
            NewArticle();
            NewExtra();
            UiLanguage.Apply(this);
        };
        Closed += (_,_) =>
        {
            _imagePreviewBitmap?.Dispose();
            _imagePreviewBitmap = null;
        };
    }

    private Control BuildUi()
    {
        var root =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("Auto,*"),
                Margin =
                    new Avalonia.Thickness(16),
                RowSpacing = 12
            };

        var header =
            new StackPanel
            {
                Spacing = 4
            };

        header.Children.Add(
            new TextBlock
            {
                Text = "STAMMDATEN",
                FontSize = 28,
                FontWeight =
                    FontWeight.Bold
            });

        header.Children.Add(
            new TextBlock
            {
                Text =
                    "Reihenfolge: 1. Gruppe → 2. Warengruppe mit MwSt. → 3. Artikel. " +
                    (InstallationEdition.ReadLocked() == "IMBISS"
                        ? "Varianten / Größen werden direkt beim Artikel gepflegt; 4. Extras. "
                        : "Varianten / Größen werden direkt beim Artikel gepflegt. ") +
                    "Die MwSt. wird bei Artikel und Extras automatisch aus der Warengruppe übernommen.",
                TextWrapping =
                    TextWrapping.Wrap,
                Opacity = 0.68
            });

        root.Children.Add(header);

        _tabs.Items.Add(
            new TabItem
            {
                Header = "1 · GRUPPE",
                Content = GroupTab()
            });

        _tabs.Items.Add(
            new TabItem
            {
                Header = "2 · WARENGRUPPE",
                Content = CategoryTab()
            });

        _tabs.Items.Add(
            new TabItem
            {
                Header = "3 · ARTIKEL",
                Content = ArticleTab()
            });

        var isImbiss = InstallationEdition.ReadLocked() == "IMBISS";
        if (isImbiss)
        {
            _tabs.Items.Add(
                new TabItem
                {
                    Header = "4 · EXTRAS",
                    Content = ExtraTab()
                });
        }

        _tabs.SelectedIndex = 0;

        Grid.SetRow(_tabs, 1);
        root.Children.Add(_tabs);

        return root;
    }

    private Control GroupTab()
    {
        var root =
            TwoColumnMasterLayout(
                "Gruppen",
                _groupList,
                out var editor);

        editor.Children.Add(
            SectionTitle(
                "1. Gruppe anlegen"));

        editor.Children.Add(
            Hint(
                "Zuerst die oberste Gruppe festlegen, z. B. Speisen, Getränke, Tabak, Backwaren."));

        Field(
            editor,
            "Gruppenname",
            _groupName);

        Field(
            editor,
            "Sortierung",
            _groupSort,
            "Kleine Zahl = weiter vorne.");

        var row = ButtonRow();

        var fresh =
            LargeButton("NEUE GRUPPE", 160);

        fresh.Click +=
            (_,_) => NewGroup();

        var save =
            LargeButton("GRUPPE SPEICHERN", 190);

        save.Click += SaveGroup;

        var delete = LargeButton("GRUPPE LÖSCHEN", 170);
        StyleDangerButton(delete);
        delete.Click += DeleteGroup;

        row.Children.Add(fresh);
        row.Children.Add(save);
        row.Children.Add(delete);
        editor.Children.Add(row);

        return root;
    }

    private Control CategoryTab()
    {
        var root =
            TwoColumnMasterLayoutWithStickyFooter(
                "Warengruppen",
                _categoryList,
                out var editor,
                out var footer);

        editor.Children.Add(
            SectionTitle(
                "2. Warengruppe anlegen"));

        editor.Children.Add(
            Hint(
                "Eine Warengruppe gehört immer zu einer Gruppe. " +
                "Die MwSt. wird HIER festgelegt und gilt automatisch für alle zugeordneten Artikel."));

        Field(
            editor,
            "Gruppe",
            _categoryGroup,
            "Ohne Gruppe kann keine Warengruppe gespeichert werden.");

        Field(
            editor,
            "Warengruppe",
            _categoryName);

        Field(
            editor,
            "MwSt.",
            _categoryVat,
            "7 % oder 19 %. Eine Änderung wird auf alle Artikel dieser Warengruppe übernommen.");

        Field(
            editor,
            "Im Haus / Außer Haus",
            _categoryImHausApplicable,
            "Nur relevant für Warengruppen mit 7 % MwSt. (Speisen): beim Verzehr vor Ort wird auf 19 % umgestellt. " +
            "Für Getränke (bereits 19 %) ohne jede Wirkung - hier abschaltbar, wenn eine 7 %-Warengruppe (z. B. Süßwaren) " +
            "bewusst NICHT dieser Regel unterliegen soll.");

        Field(
            editor,
            "Sortierung",
            _categorySort,
            "Wird beim Speichern übernommen. Einfacher: mit „▲ NACH OBEN“ / „▼ NACH UNTEN“ unten verschieben " +
            "– das nummeriert alle Warengruppen automatisch sauber durch.");

        Field(
            editor,
            "Küchenstation",
            _categoryKitchenStation,
            "Bestellungen aus dieser Warengruppe werden an den hier gewählten Küchendrucker geroutet. " +
            "„(Standard-Küchendrucker)“ nutzt weiterhin den einen konfigurierten Küchendrucker.");

        editor.Children.Add(CategoryColorEditor());

        var taxInfo =
            new Border
            {
                Background =
                    new SolidColorBrush(
                        Color.Parse("#173A32")),
                CornerRadius =
                    new Avalonia.CornerRadius(8),
                Padding =
                    new Avalonia.Thickness(12),
                Margin =
                    new Avalonia.Thickness(0, 8, 0, 0),
                Child =
                    new TextBlock
                    {
                        Text =
                            "MwSt.-Regel: Der Artikel besitzt keine eigene MwSt.-Auswahl. " +
                            "Er übernimmt den Steuersatz der Warengruppe.",
                        TextWrapping =
                            TextWrapping.Wrap,
                        Foreground = AppTheme.AccentTeal
                    }
            };

        editor.Children.Add(taxInfo);

        // R64 FIX1: zwei ruhige Zeilen statt fünf breiter Touch-Buttons in
        // einer einzigen Zeile. So bleibt die Bedienung auch bei 1180 px sauber.
        var moveRow = ButtonRow();
        var moveUp = LargeButton("▲ NACH OBEN", 170);
        moveUp.Click += MoveCategoryUp;
        var moveDown = LargeButton("▼ NACH UNTEN", 170);
        moveDown.Click += MoveCategoryDown;
        moveRow.Children.Add(moveUp);
        moveRow.Children.Add(moveDown);

        var actionRow = ButtonRow();

        var fresh =
            LargeButton(
                "NEUE WARENGRUPPE",
                190);

        fresh.Click +=
            (_,_) => NewCategory();

        var save =
            LargeButton(
                "WARENGRUPPE SPEICHERN",
                220);

        save.Click += SaveCategory;

        var delete = LargeButton("WARENGRUPPE LÖSCHEN", 210);
        StyleDangerButton(delete);
        delete.Click += DeleteCategory;

        var offer = LargeButton("ANGEBOT", 150);
        offer.Background = Brush.Parse("#0F7A55");
        offer.BorderBrush = AppTheme.AccentTeal;
        offer.Click += OpenCategoryPromotion;

        actionRow.Children.Add(fresh);
        actionRow.Children.Add(save);
        actionRow.Children.Add(offer);
        actionRow.Children.Add(delete);
        footer.Children.Add(moveRow);
        footer.Children.Add(actionRow);

        return root;
    }

    private Control ArticleTab()
    {
        var root =
            ThreeColumnArticleLayoutWithStickyFooter(
                "Warengruppen",
                _articleCategoryList,
                "Artikel in Warengruppe",
                _articleList,
                out var editor,
                out var footer);

        editor.Children.Add(
            SectionTitle(
                "3. Artikel anlegen / bearbeiten"));

        editor.Children.Add(
            Hint(
                "Links zuerst die Warengruppe wählen. In der mittleren Liste erscheinen danach nur die Artikel dieser Warengruppe. Rechts bleibt die Artikel-Erfassung zum schnellen Anlegen, Bearbeiten und Löschen sichtbar."));

        Field(
            editor,
            "Artikel suchen / Scanner",
            _articleSearch,
            "Name, EAN oder Artikel-Nr. eingeben. Bei einem Scanner einfach EAN lesen; ENTER wählt den exakten Treffer in der aktuell gewählten Warengruppe.");

        Field(
            editor,
            "Warengruppe",
            _articleCategory,
            "Wird automatisch aus der linken Auswahl übernommen, kann hier aber bei Bedarf direkt geändert werden.");

        var vatPanel =
            new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions("180,*"),
                Margin =
                    new Avalonia.Thickness(0, 4, 0, 6)
            };

        vatPanel.Children.Add(
            new TextBlock
            {
                Text = "MwSt. automatisch",
                VerticalAlignment =
                    Avalonia.Layout.VerticalAlignment.Center,
                Opacity = 0.68
            });

        Grid.SetColumn(
            _articleVat,
            1);

        vatPanel.Children.Add(
            _articleVat);

        editor.Children.Add(vatPanel);

        Field(
            editor,
            "Artikelname",
            _name);

        Field(
            editor,
            "Barcode / EAN",
            _barcode);

        Field(
            editor,
            "Artikel-Nr. (automatisch)",
            _sku,
            "TOR vergibt für jeden Artikel automatisch eine fortlaufende Artikelnummer. Bestehende Nummern bleiben erhalten.");

        editor.Children.Add(_soldByWeight);
        editor.Children.Add(_weightHint);

        Field(
            editor,
            "Verkaufspreis €",
            _price,
            "Bei Gewichtsartikeln ist dies der Preis pro kg. Beispiel: 19,90 = 19,90 €/kg.");

        Field(
            editor,
            "Einkaufspreis €",
            _purchasePrice,
            "Optional. Wird für Warenwert und Bestandsauswertung verwendet; ändert den Verkaufspreis nicht.");

        if (InstallationEdition.ReadLocked() == "KIOSK")
        {
            Field(
                editor,
                "Pfand €",
                _pfand);
        }

        Field(
            editor,
            "Einheit",
            _unit);

        Field(
            editor,
            "Bestand / Anzahl",
            _stock,
            "Beim Anlegen kann der Anfangsbestand direkt erfasst werden. Bei späteren Preis-/Namensänderungen bleibt der vorhandene Bestand unverändert, solange dieses Feld nicht geändert wird.");

        Field(
            editor,
            "Mindestbestand / Warnung ab",
            _minStock,
            "0 = keine Warnung. Beispiel: 5 zeigt eine Bestandswarnung, sobald 5 Stück oder weniger vorhanden sind.");

        var image =
            LargeButton(
                "PRODUKTBILD AUSWÄHLEN",
                220);

        image.Click += ChooseImage;

        editor.Children.Add(image);
        editor.Children.Add(_imageText);
        editor.Children.Add(new Border
        {
            Height = 164,
            Background = AppTheme.SurfacePanel,
            BorderBrush = AppTheme.PanelBorder,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(9),
            Padding = new Avalonia.Thickness(6),
            Child = _imagePreview
        });

        editor.Children.Add(VariantEditorSection());
        if (InstallationEdition.ReadLocked() == "IMBISS")
            editor.Children.Add(ComboEditorSection());

        var row = ButtonRow();

        var fresh =
            LargeButton(
                "NEUER ARTIKEL",
                160);

        fresh.Click +=
            (_,_) => NewArticle();

        var save =
            LargeButton(
                "ARTIKEL SPEICHERN",
                190);

        save.Click += SaveArticle;

        var close =
            LargeButton(
                "SCHLIESSEN",
                150);

        close.Click +=
            (_,_) => Close(_saved);

        var delete = LargeButton("ARTIKEL LÖSCHEN", 170);
        StyleDangerButton(delete);
        delete.Click += DeleteArticle;

        var offer = LargeButton("ANGEBOT", 160);
        offer.Background = Brush.Parse("#0F7A55");
        offer.BorderBrush = AppTheme.AccentTeal;
        offer.Click += OpenArticlePromotion;

        row.Children.Add(fresh);
        row.Children.Add(save);
        row.Children.Add(offer);
        row.Children.Add(delete);
        row.Children.Add(close);
        footer.Children.Add(row);

        return root;
    }

    private Control ExtraTab()
    {
        var root =
            TwoColumnMasterLayoutWithStickyFooter(
                "Extras",
                _extraList,
                out var editor,
                out var footer);

        editor.Children.Add(SectionTitle("4. Extra anlegen"));
        editor.Children.Add(Hint(
            "Extras erscheinen nur in der IMBISS-Version, z. B. Extra Käse, " +
            "Extra Ketchup oder Extra Fleisch. Die MwSt. wird aus der Warengruppe übernommen."));

        Field(editor, "Warengruppe", _extraCategory);
        Field(editor, "Extra-Name", _extraName);
        Field(editor, "Preis €", _extraPrice);
        Field(editor, "Sortierung", _extraSort);
        Field(editor, "MwSt. automatisch", _extraVat);

        var row = ButtonRow();
        var fresh = LargeButton("NEUES EXTRA", 160);
        fresh.Click += (_,_) => NewExtra();
        var save = LargeButton("EXTRA SPEICHERN", 190);
        save.Click += SaveExtra;
        row.Children.Add(fresh);
        row.Children.Add(save);
        footer.Children.Add(row);

        return root;
    }

    private Control VariantEditorSection()
    {
        _variantList.MinHeight = 130;
        _variantList.MaxHeight = 210;

        var buttons = ButtonRow();

        var add = LargeButton("+ VARIANTE", 150);
        add.Click += AddVariant;

        var edit = LargeButton("BEARBEITEN", 150);
        edit.Click += EditVariant;

        var del = LargeButton("LÖSCHEN", 140);
        del.Click += (_,_) =>
        {
            if (_variantList.SelectedIndex >= 0)
                _variants.RemoveAt(_variantList.SelectedIndex);
        };

        buttons.Children.Add(add);
        buttons.Children.Add(edit);
        buttons.Children.Add(del);

        return new Border
        {
            Background = AppTheme.SurfacePanel,
            BorderBrush = AppTheme.PanelBorder,
            BorderThickness = new Avalonia.Thickness(1),
            CornerRadius = new Avalonia.CornerRadius(9),
            Padding = new Avalonia.Thickness(14),
            Margin = new Avalonia.Thickness(0, 10, 0, 4),
            Child = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "VARIANTEN / GRÖSSEN",
                        FontSize = 19,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = "Direkt mit diesem Artikel speichern, z. B. Klein / Mittel / Groß. Kein separates Varianten-Menü mehr nötig.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.68
                    },
                    _variantList,
                    buttons
                }
            }
        };
    }

    private Control ComboEditorSection()
    {
        _comboList.MinHeight=140; _comboList.MaxHeight=250;
        var buttons=ButtonRow();
        var add=LargeButton("+ ARTIKEL HINZUFÜGEN",200); add.Click+=AddComboItem;
        var edit=LargeButton("MENGE ÄNDERN",150); edit.Click+=EditComboItem;
        var del=LargeButton("LÖSCHEN",130); del.Click+=(_,_) =>
        {
            if(_comboList.SelectedIndex<0) return;
            _comboItems.RemoveAt(_comboList.SelectedIndex);
            RefreshComboSummary();
        };
        buttons.Children.Add(add);buttons.Children.Add(edit);buttons.Children.Add(del);

        return new Border
        {
            Background=AppTheme.SurfacePanel,BorderBrush=AppTheme.PanelBorder,BorderThickness=new Avalonia.Thickness(1),
            CornerRadius=new Avalonia.CornerRadius(9),Padding=new Avalonia.Thickness(14),Margin=new Avalonia.Thickness(0,10,0,4),
            Child=new StackPanel{Spacing=8,Children={
                new TextBlock{Text="MENÜ / COMBO",FontSize=19,FontWeight=FontWeight.Bold},
                new TextBlock{
                    Text="Bestandteile werden direkt aus dem normalen Artikelstamm gewählt. Der Menü-Verkaufspreis oben bleibt eigenständig; die normalen Artikelpreise dienen nur als Marktwert für Bestand und MwSt.-Aufteilung.",
                    TextWrapping=TextWrapping.Wrap,Opacity=0.68},
                new TextBlock{
                    Text="Auswahl: Feld leer lassen = fester Bestandteil. Gleichen Gruppennamen verwenden (z. B. GETRÄNK) = Kunde/Kassierer wählt beim Verkauf genau einen Artikel aus dieser Gruppe. Kein Aufpreis-Feld.",
                    TextWrapping=TextWrapping.Wrap,Foreground=AppTheme.AccentTeal},
                new TextBlock{Text="Auswahlgruppe (optional)",Opacity=.7},
                _comboChoiceGroup,
                _comboList,
                _comboSummary,
                buttons
            }}
        };
    }

    private async void AddComboItem(object? sender,RoutedEventArgs e)
    {
        var excluded=_selectedArticle?.Id??0;
        var candidates=_catalog.Products
            .Where(x=>x.Id!=excluded && !x.IsCombo)
            .OrderBy(x=>x.Name)
            .ToArray();

        var chosen=await new ComboComponentWindow(candidates).ShowDialog<ComboComponentResult?>(this);
        if(chosen is null)return;

        var group=(_comboChoiceGroup.Text??"").Trim().ToUpperInvariant();
        var existing=_comboItems.FirstOrDefault(x=>x.ComponentProductId==chosen.ProductId);
        if(existing is not null)
        {
            if (!string.Equals(existing.ChoiceGroup,group,StringComparison.OrdinalIgnoreCase))
            {
                _comboSummary.Text="Dieser Artikel ist bereits im Menü vorhanden. Bitte denselben Artikel nicht gleichzeitig fest und als Auswahloption verwenden.";
                _comboSummary.Foreground=AppTheme.WarningAmber;
                return;
            }

            var index=_comboItems.IndexOf(existing);
            _comboItems[index]=existing with {Quantity=existing.Quantity+chosen.Quantity};
            RefreshComboSummary();
            return;
        }

        var product=candidates.First(x=>x.Id==chosen.ProductId);
        _comboItems.Add(new ComboRow(
            product.Id,
            product.Name,
            chosen.Quantity,
            product.BasePriceCents,
            product.VatRate,
            product.ImHausApplicable,
            group));
        RefreshComboSummary();
    }

    private async void EditComboItem(object? sender,RoutedEventArgs e)
    {
        var index=_comboList.SelectedIndex;if(index<0)return;
        var current=_comboItems[index];
        var chosen=await new ComboQuantityWindow(current.Name,current.Quantity).ShowDialog<decimal?>(this);
        if(chosen is >0m)
        {
            _comboItems[index]=current with {Quantity=chosen.Value};
            RefreshComboSummary();
        }
    }

    private ProductComboItem[] BuildComboItems(long productId) =>
        _comboItems.Select((row,index)=>new ProductComboItem(
            productId,
            row.ComponentProductId,
            row.Name,
            row.Quantity,
            index,
            row.ChoiceGroup)).ToArray();

    private MenuComponentSnapshot[] DefaultMenuSelection()
    {
        var selected = new List<ComboRow>();
        selected.AddRange(_comboItems.Where(x => string.IsNullOrWhiteSpace(x.ChoiceGroup)));
        foreach (var group in _comboItems
                     .Where(x => !string.IsNullOrWhiteSpace(x.ChoiceGroup))
                     .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase))
        {
            selected.Add(group.OrderBy(x => x.Name).First());
        }

        return selected.Select(x => new MenuComponentSnapshot(
            x.ComponentProductId,
            x.Name,
            x.Quantity,
            x.NormalPriceCents,
            x.VatRate,
            x.ImHausApplicable,
            x.ChoiceGroup)).ToArray();
    }

    private long MinimumMenuMarketValue()
    {
        var fixedTotal = _comboItems
            .Where(x => string.IsNullOrWhiteSpace(x.ChoiceGroup))
            .Sum(x => (long)Math.Round(x.Quantity*x.NormalPriceCents,MidpointRounding.AwayFromZero));

        var choices = _comboItems
            .Where(x => !string.IsNullOrWhiteSpace(x.ChoiceGroup))
            .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase)
            .Sum(g => g.Min(x =>
                (long)Math.Round(x.Quantity*x.NormalPriceCents,MidpointRounding.AwayFromZero)));

        return fixedTotal + choices;
    }

    private long MaximumMenuMarketValue()
    {
        var fixedTotal = _comboItems
            .Where(x => string.IsNullOrWhiteSpace(x.ChoiceGroup))
            .Sum(x => (long)Math.Round(x.Quantity*x.NormalPriceCents,MidpointRounding.AwayFromZero));

        var choices = _comboItems
            .Where(x => !string.IsNullOrWhiteSpace(x.ChoiceGroup))
            .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase)
            .Sum(g => g.Max(x =>
                (long)Math.Round(x.Quantity*x.NormalPriceCents,MidpointRounding.AwayFromZero)));

        return fixedTotal + choices;
    }

    private void ValidateChoiceGroups()
    {
        foreach (var group in _comboItems
                     .Where(x => !string.IsNullOrWhiteSpace(x.ChoiceGroup))
                     .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() < 2)
                throw new InvalidOperationException(
                    $"Auswahlgruppe {group.Key} benötigt mindestens zwei Artikel.");
        }
    }

    private void RefreshComboSummary()
    {
        if (_comboItems.Count == 0)
        {
            _comboSummary.Text = "Kein Menü definiert. Der Artikel wird normal mit seiner Warengruppen-MwSt. verkauft.";
            _comboSummary.Foreground = AppTheme.AccentTeal;
            return;
        }

        try
        {
            ValidateChoiceGroups();
        }
        catch (Exception ex)
        {
            _comboSummary.Text = "FEHLER: " + ex.Message;
            _comboSummary.Foreground = AppTheme.WarningAmber;
            return;
        }

        if (!Formatting.TryParseMoney(_price.Text, out var menuPrice) || menuPrice < 0)
        {
            _comboSummary.Text = "Bitte gültigen Menü-Verkaufspreis eingeben.";
            _comboSummary.Foreground = AppTheme.WarningAmber;
            return;
        }

        var minTotal = MinimumMenuMarketValue();
        var maxTotal = MaximumMenuMarketValue();
        var selection = DefaultMenuSelection();

        var transient = new Product
        {
            Id = _selectedArticle?.Id ?? 0,
            Name = string.IsNullOrWhiteSpace(_name.Text) ? "Menü" : _name.Text!.Trim(),
            BasePriceCents = menuPrice,
            VatRate = (_articleCategory.SelectedItem as Category)?.VatRate ?? 19m,
            ComboItems = BuildComboItems(_selectedArticle?.Id ?? 0)
        };

        var takeAway = MenuVatPolicy.Analyze(
            transient,_catalog.Products,imHaus:false,menuGrossCents:menuPrice,selectedComponents:selection);
        var inHouse = MenuVatPolicy.Analyze(
            transient,_catalog.Products,imHaus:true,menuGrossCents:menuPrice,selectedComponents:selection);

        var groups = _comboItems
            .Where(x => !string.IsNullOrWhiteSpace(x.ChoiceGroup))
            .GroupBy(x => x.ChoiceGroup, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}: {g.Count()} Optionen")
            .ToArray();

        static string AllocationText(MenuVatAnalysis analysis) =>
            analysis.IsValid
                ? string.Join(" · ", analysis.Allocations.Select(x =>
                    $"{x.VatRate:0}% {Formatting.Money(x.GrossCents)}"))
                : "FEHLER: " + analysis.Message;

        var marketRange = minTotal == maxTotal
            ? Formatting.Money(minTotal)
            : $"{Formatting.Money(minTotal)} – {Formatting.Money(maxTotal)}";
        var maxChoiceDifference = Math.Max(0L, maxTotal - minTotal);
        var highestMenuPrice = menuPrice + maxChoiceDifference;
        var salePriceRange = maxChoiceDifference == 0
            ? Formatting.Money(menuPrice)
            : $"{Formatting.Money(menuPrice)} – {Formatting.Money(highestMenuPrice)}";
        var advantage = minTotal - menuPrice;

        _comboSummary.Text =
            $"Einzelpreise je nach Auswahl: {marketRange}\n" +
            $"Menü-Verkaufspreis automatisch: {salePriceRange} · " +
            $"Menüvorteil bleibt {Formatting.Money(Math.Max(0,advantage))}\n" +
            "Preisregel: Grundpreis = günstigste Auswahl; teurere Artikeloptionen erhöhen den Menüpreis automatisch nur um ihre echte Artikel-Preisdifferenz." +
            (groups.Length==0 ? "" : $"\nAuswahlgruppen: {string.Join(" · ",groups)}") +
            $"\nMwSt.-Beispiel Außer Haus (günstigste Auswahl): {AllocationText(takeAway)}" +
            $"\nMwSt.-Beispiel Im Haus (günstigste Auswahl): {AllocationText(inHouse)}";

        _comboSummary.Foreground =
            takeAway.IsValid && inHouse.IsValid && menuPrice <= minTotal
                ? AppTheme.AccentTeal
                : AppTheme.WarningAmber;
    }

    private static Grid ThreeColumnArticleLayoutWithStickyFooter(
        string categoryTitle,
        ListBox categoryList,
        string articleTitle,
        ListBox articleList,
        out StackPanel editor,
        out StackPanel footer)
    {
        var root =
            new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions("250,360,*"),
                ColumnSpacing = 16,
                Margin =
                    new Avalonia.Thickness(12)
            };

        var left =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("Auto,*"),
                RowSpacing = 8
            };

        left.Children.Add(
            new TextBlock
            {
                Text = categoryTitle,
                FontSize = 21,
                FontWeight = FontWeight.Bold
            });

        Grid.SetRow(categoryList, 1);
        left.Children.Add(categoryList);

        var middle =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("Auto,*"),
                RowSpacing = 8
            };

        middle.Children.Add(
            new TextBlock
            {
                Text = articleTitle,
                FontSize = 21,
                FontWeight = FontWeight.Bold
            });

        Grid.SetRow(articleList, 1);
        middle.Children.Add(articleList);
        Grid.SetColumn(middle, 1);

        var right =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("*,Auto"),
                Margin =
                    new Avalonia.Thickness(0,0,0,0)
            };

        editor =
            new StackPanel
            {
                Spacing = 8,
                Margin =
                    new Avalonia.Thickness(0,0,8,8)
            };

        var scroll =
            new ScrollViewer
            {
                Content = editor,
                VerticalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };

        right.Children.Add(scroll);

        footer =
            new StackPanel
            {
                Orientation =
                    Avalonia.Layout.Orientation.Vertical
            };

        var footerBorder =
            new Border
            {
                Background =
                    Brush.Parse("#101925"),
                BorderBrush =
                    Brush.Parse("#263446"),
                BorderThickness =
                    new Avalonia.Thickness(0,1,0,0),
                Padding =
                    new Avalonia.Thickness(0,10,0,0),
                Child = footer
            };

        Grid.SetRow(footerBorder, 1);
        right.Children.Add(footerBorder);

        Grid.SetColumn(right, 2);
        root.Children.Add(left);
        root.Children.Add(middle);
        root.Children.Add(right);

        return root;
    }

    private static Grid TwoColumnMasterLayout(
        string listTitle,
        ListBox list,
        out StackPanel editor)
    {
        var root =
            new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions(
                        "360,*"),
                ColumnSpacing = 16,
                Margin =
                    new Avalonia.Thickness(12)
            };

        var left =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions(
                        "Auto,*"),
                RowSpacing = 8
            };

        left.Children.Add(
            new TextBlock
            {
                Text = listTitle,
                FontSize = 21,
                FontWeight =
                    FontWeight.Bold
            });

        Grid.SetRow(
            list,
            1);

        left.Children.Add(list);

        editor =
            new StackPanel
            {
                Spacing = 8,
                Margin =
                    new Avalonia.Thickness(
                        12,0,0,0)
            };

        Grid.SetColumn(
            editor,
            1);

        root.Children.Add(left);
        root.Children.Add(editor);

        return root;
    }

    private static Grid TwoColumnMasterLayoutWithStickyFooter(
        string listTitle,
        ListBox list,
        out StackPanel editor,
        out StackPanel footer)
    {
        var root =
            new Grid
            {
                ColumnDefinitions =
                    new ColumnDefinitions("360,*"),
                ColumnSpacing = 16,
                Margin =
                    new Avalonia.Thickness(12)
            };

        var left =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("Auto,*"),
                RowSpacing = 8
            };

        left.Children.Add(
            new TextBlock
            {
                Text = listTitle,
                FontSize = 21,
                FontWeight = FontWeight.Bold
            });

        Grid.SetRow(list, 1);
        left.Children.Add(list);

        var right =
            new Grid
            {
                RowDefinitions =
                    new RowDefinitions("*,Auto"),
                Margin =
                    new Avalonia.Thickness(12,0,0,0)
            };

        editor =
            new StackPanel
            {
                Spacing = 8,
                Margin =
                    new Avalonia.Thickness(0,0,8,8)
            };

        var scroll =
            new ScrollViewer
            {
                Content = editor,
                VerticalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled
            };

        right.Children.Add(scroll);

        footer =
            new StackPanel
            {
                Orientation =
                    Avalonia.Layout.Orientation.Vertical
            };

        var footerBorder =
            new Border
            {
                Background =
                    Brush.Parse("#101925"),
                BorderBrush =
                    Brush.Parse("#263446"),
                BorderThickness =
                    new Avalonia.Thickness(0,1,0,0),
                Padding =
                    new Avalonia.Thickness(0,10,0,0),
                Child = footer
            };

        Grid.SetRow(footerBorder, 1);
        right.Children.Add(footerBorder);

        Grid.SetColumn(right, 1);
        root.Children.Add(left);
        root.Children.Add(right);

        return root;
    }

    private static TextBlock SectionTitle(
        string text) =>
        new()
        {
            Text = text,
            FontSize = 23,
            FontWeight =
                FontWeight.Bold
        };

    private static TextBlock Hint(
        string text) =>
        new()
        {
            Text = text,
            TextWrapping =
                TextWrapping.Wrap,
            Opacity = 0.68,
            Margin =
                new Avalonia.Thickness(
                    0,0,0,8)
        };

    private static StackPanel ButtonRow() =>
        new()
        {
            Orientation =
                Avalonia.Layout.Orientation.Horizontal,
            Spacing = 8,
            Margin =
                new Avalonia.Thickness(
                    0,12,0,0)
        };

    private static Button LargeButton(
        string text,
        double width) =>
        new()
        {
            Content = text,
            MinWidth = width,
            MinHeight = 50,
            FontWeight =
                FontWeight.Bold
        };

    private static void Field(
        Panel panel,
        string label,
        Control control,
        string hint = "")
    {
        panel.Children.Add(
            new TextBlock
            {
                Text = label,
                Opacity = 0.7
            });

        panel.Children.Add(control);

        if (!string.IsNullOrWhiteSpace(hint))
        {
            panel.Children.Add(
                new TextBlock
                {
                    Text = hint,
                    TextWrapping =
                        TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.52
                });
        }
    }

    private void RefreshAllBindings()
    {
        var currentGroupId =
            (_categoryGroup.SelectedItem
                as ProductGroup)?.Id;

        var currentCategoryId =
            (_articleCategory.SelectedItem
                as Category)?.Id
            ?? (_selectedArticleCategoryForList?.Id);

        var currentExtraCategoryId =
            (_extraCategory.SelectedItem
                as Category)?.Id;

        _categoryGroup.ItemsSource =
            _catalog.Groups;

        _categoryGroup.DisplayMemberBinding =
            new Avalonia.Data.Binding(
                nameof(ProductGroup.Name));

        _articleCategory.ItemsSource =
            _catalog.Categories;

        _articleCategory.DisplayMemberBinding =
            new Avalonia.Data.Binding(
                nameof(Category.Name));

        _extraCategory.ItemsSource =
            _catalog.Categories;

        _extraCategory.DisplayMemberBinding =
            new Avalonia.Data.Binding(
                nameof(Category.Name));

        _groupList.ItemsSource =
            _catalog.Groups
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .Select(x =>
                    new GroupRow(
                        x.Id,
                        x.Name,
                        x.SortOrder))
                .ToArray();

        // R64 FIX1: Diese Liste entspricht bewusst der Reihenfolge auf dem
        // Kassenbildschirm, damit ▲/▼ für den Bediener sichtbar nachvollziehbar ist.
        _categoryList.ItemsSource =
            _catalog.Categories
                .OrderBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .Select(x =>
                    new CategoryRow(
                        x.Id,
                        GroupName(x.GroupId),
                        x.Name,
                        x.VatRate,
                        x.SortOrder))
                .ToArray();

        _articleCategoryList.ItemsSource =
            _catalog.Categories
                .OrderBy(x => GroupSort(x.GroupId))
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Name)
                .Select(x => new CategoryRow(
                    x.Id,
                    GroupName(x.GroupId),
                    x.Name,
                    x.VatRate,
                    x.SortOrder))
                .ToArray();

        RefreshExtraList();

        if (currentGroupId is long groupId)
        {
            _categoryGroup.SelectedItem =
                _catalog.Groups.FirstOrDefault(
                    x => x.Id == groupId);
        }

        if (currentCategoryId is long categoryId)
        {
            var category = _catalog.Categories.FirstOrDefault(
                x => x.Id == categoryId);
            _articleCategory.SelectedItem = category;
            _selectedArticleCategoryForList = category;
            _articleCategoryList.SelectedItem =
                (_articleCategoryList.ItemsSource as IEnumerable<CategoryRow>)?
                    .FirstOrDefault(x => x.Id == categoryId);
        }
        else if ((_articleCategoryList.ItemsSource as IEnumerable<CategoryRow>)?.Any() == true)
        {
            _articleCategoryList.SelectedIndex = 0;
            if (_articleCategoryList.SelectedItem is CategoryRow row)
            {
                var category = _catalog.Categories.FirstOrDefault(x => x.Id == row.Id);
                _articleCategory.SelectedItem = category;
                _selectedArticleCategoryForList = category;
            }
        }

        if (currentExtraCategoryId is long extraCategoryId)
        {
            _extraCategory.SelectedItem =
                _catalog.Categories.FirstOrDefault(
                    x => x.Id == extraCategoryId);
        }

        RefreshArticleList();
        RefreshInheritedVat();
        RefreshExtraVat();
    }

    private void RefreshArticleList()
    {
        var categories =
            _catalog.Categories.ToDictionary(
                x => x.Id);

        var selectedCategoryId =
            (_articleCategoryList.SelectedItem as CategoryRow)?.Id
            ?? _selectedArticleCategoryForList?.Id;

        var query = (_articleSearch.Text ?? "").Trim();
        IEnumerable<Product> products = _catalog.Products;

        if (selectedCategoryId is long categoryId)
            products = products.Where(x => x.CategoryId == categoryId);

        if (query.Length > 0)
        {
            products = products.Where(x =>
                x.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                x.Sku.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        var rows = products
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x =>
            {
                categories.TryGetValue(x.CategoryId, out var cat);

                return new ArticleRow(
                    x.Id,
                    cat is null
                        ? ""
                        : GroupName(cat.GroupId),
                    cat?.Name ?? "",
                    x.Name,
                    x.Barcode,
                    x.Sku,
                    x.StockQuantity,
                    x.MinStockQuantity,
                    x.BasePriceCents +
                    x.PfandCents,
                    cat?.VatRate ??
                    x.VatRate);
            })
            .ToArray();

        _articleList.ItemsSource = rows;

        if (_selectedArticle is not null)
        {
            _articleList.SelectedItem =
                rows.FirstOrDefault(x => x.Id == _selectedArticle.Id);
        }
    }

    private void OnArticleCategoryFilterSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_syncingArticleCategoryUi)
            return;

        _syncingArticleCategoryUi = true;
        try
        {
            if (_articleCategoryList.SelectedItem is CategoryRow row)
            {
                var category = _catalog.Categories.FirstOrDefault(x => x.Id == row.Id);
                _selectedArticleCategoryForList = category;
                _articleCategory.SelectedItem = category;

                if (_selectedArticle is not null && _selectedArticle.CategoryId != row.Id)
                    _selectedArticle = null;
            }
            else
            {
                _selectedArticleCategoryForList = null;
            }

            RefreshArticleList();
            RefreshInheritedVat();
        }
        finally
        {
            _syncingArticleCategoryUi = false;
        }
    }

    private void SelectExactArticleFromSearch()
    {
        var query = (_articleSearch.Text ?? "").Trim();
        if (query.Length == 0)
            return;

        var product = _catalog.Products.FirstOrDefault(x =>
            string.Equals(x.Barcode, query, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(x.Sku, query, StringComparison.OrdinalIgnoreCase));
        if (product is null)
            return;

        RefreshArticleList();
        _articleList.SelectedItem =
            (_articleList.ItemsSource as IEnumerable<ArticleRow>)?
            .FirstOrDefault(x => x.Id == product.Id);
    }

    private void RefreshExtraList()
    {
        var categories = _catalog.Categories.ToDictionary(x => x.Id);
        _extraList.ItemsSource = _extras
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .Select(x => new ExtraRow(
                x.Id,
                categories.TryGetValue(x.CategoryId, out var category)
                    ? category.Name
                    : "Ohne Warengruppe",
                x.Name,
                x.PriceCents,
                x.VatRate,
                x.SortOrder))
            .ToArray();
    }

    private string GroupName(
        long groupId) =>
        _catalog.Groups
            .FirstOrDefault(
                x => x.Id == groupId)?
            .Name ?? "Ohne Gruppe";

    private int GroupSort(
        long groupId) =>
        _catalog.Groups
            .FirstOrDefault(
                x => x.Id == groupId)?
            .SortOrder ?? int.MaxValue;

    private void NewGroup()
    {
        _selectedGroup = null;
        _groupList.SelectedIndex = -1;
        _groupName.Text = "";
        _groupSort.Text = "0";
    }

    private Control CategoryColorEditor()
    {
        var panel = new StackPanel
        {
            Spacing = 8,
            Margin = new Avalonia.Thickness(0, 8, 0, 8)
        };

        panel.Children.Add(new TextBlock
        {
            Text = "Farbe der Warengruppen-Taste",
            FontWeight = FontWeight.Bold,
            FontSize = 15
        });

        panel.Children.Add(new TextBlock
        {
            Text = "Jede Warengruppe kann eine eigene Farbe erhalten. Schnellfarbe wählen oder einen beliebigen HEX-Wert eingeben.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .72,
            FontSize = 13
        });

        var line = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            Spacing = 10
        };
        _categoryColor.Width = 140;
        _categoryColor.PlaceholderText = "#RRGGBB";
        line.Children.Add(_categoryColor);
        line.Children.Add(_categoryColorPreview);
        panel.Children.Add(line);

        panel.Children.Add(new TextBlock
        {
            Text = "SCHNELLFARBEN",
            FontWeight = FontWeight.SemiBold,
            FontSize = 12,
            Opacity = .78,
            Margin = new Avalonia.Thickness(0, 4, 0, 0)
        });

        var palette = new WrapPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            MaxWidth = 560
        };
        foreach (var hex in CategoryPalette)
        {
            var button = new Button
            {
                Width = 38,
                Height = 34,
                Margin = new Avalonia.Thickness(0, 0, 6, 6),
                Padding = new Avalonia.Thickness(0),
                Background = new SolidColorBrush(Color.Parse(hex)),
                BorderBrush = Brushes.White,
                BorderThickness = new Avalonia.Thickness(1),
                Tag = hex
            };
            ToolTip.SetTip(button, hex);
            button.Click += (_, _) => _categoryColor.Text = hex;
            palette.Children.Add(button);
        }
        panel.Children.Add(palette);
        panel.Children.Add(new TextBlock
        {
            Text = "48 Schnellfarben + freie Farbauswahl: z. B. #C0392B, #00AEEF oder #F4C542. Damit stehen praktisch alle RGB-Farben zur Verfügung.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = .66,
            FontSize = 12
        });
        return panel;
    }

    private static readonly string[] CategoryPalette =
    {
        // Blau / Cyan
        "#0D47A1", "#1565C0", "#1976D2", "#1E88E5", "#0277BD", "#00838F",
        "#006064", "#0097A7",
        // Grün
        "#1B5E20", "#2E7D32", "#388E3C", "#43A047", "#558B2F", "#689F38",
        "#33691E", "#00796B",
        // Gelb / Orange / Braun
        "#F9A825", "#F57F17", "#EF6C00", "#F4511E", "#E65100", "#BF6D24",
        "#795548", "#5D4037",
        // Rot / Pink
        "#B71C1C", "#C62828", "#D32F2F", "#E53935", "#AD1457", "#C2185B",
        "#D81B60", "#880E4F",
        // Violett
        "#4A148C", "#6A1B9A", "#7B1FA2", "#8E24AA", "#4527A0", "#512DA8",
        "#5E35B1", "#311B92",
        // Neutral / Petrol
        "#17466A", "#263238", "#37474F", "#455A64", "#546E7A", "#3E5F66",
        "#2F4F4F", "#607D8B"
    };

    private string NextSuggestedCategoryColor()
    {
        var used = _catalog.Categories
            .Select(x => NormalizeCategoryColor(x.TileColor))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return CategoryPalette.FirstOrDefault(x => !used.Contains(x))
            ?? CategoryPalette[_catalog.Categories.Count % CategoryPalette.Length];
    }

    private static string NormalizeCategoryColor(string? value)
    {
        var text = (value ?? "").Trim().ToUpperInvariant();
        if (text.Length == 7 && text[0] == '#' &&
            text.Skip(1).All(Uri.IsHexDigit))
            return text;
        return "#17466A";
    }

    private void RefreshCategoryColorPreview()
    {
        try
        {
            _categoryColorPreview.Background = new SolidColorBrush(
                Color.Parse(NormalizeCategoryColor(_categoryColor.Text)));
        }
        catch
        {
            _categoryColorPreview.Background = new SolidColorBrush(Color.Parse("#17466A"));
        }
    }

    private void NewCategory()
    {
        _selectedCategory = null;
        _categoryList.SelectedIndex = -1;
        _categoryName.Text = "";
        _categorySort.Text = "0";
        _categoryVat.SelectedItem = "19";
        _categoryImHausApplicable.IsChecked = true;
        _categoryKitchenStation.SelectedItem = KitchenStations.None;
        _categoryColor.Text = NextSuggestedCategoryColor();

        _categoryGroup.SelectedIndex =
            _catalog.Groups.Count > 0
                ? 0
                : -1;
    }

    private void ApplyWeightMode()
    {
        var weighted = _soldByWeight.IsChecked == true;
        _weightHint.IsVisible = weighted;
        _weightHint.Text = weighted
            ? UiLanguage.T("Gewichtsartikel: Preis = €/kg. Verkauf kann ohne angeschlossene Waage manuell in g oder kg eingegeben werden. Bestand und Mindestbestand werden intern in kg geführt.")
            : "";

        if (weighted)
        {
            _unit.SelectedItem = "kg";
            _unit.IsEnabled = false;
            _pfand.Text = "0,00";
            _pfand.IsEnabled = false;
        }
        else
        {
            _unit.IsEnabled = true;
            _pfand.IsEnabled = InstallationEdition.ReadLocked() == "KIOSK";
            if (string.Equals(_unit.SelectedItem?.ToString(), "kg", StringComparison.OrdinalIgnoreCase))
                _unit.SelectedItem = "Stück";
        }
    }

    private void NewArticle()
    {
        _selectedArticle = null;
        _articleList.SelectedIndex = -1;

        _name.Text = "";
        _barcode.Text =
            _initialBarcode;
        _sku.Text = "AUTO";
        _price.Text = "";
        _purchasePrice.Text = "0,00";
        _pfand.Text = "0,00";
        _soldByWeight.IsChecked = false;
        _unit.IsEnabled = true;
        _unit.SelectedIndex = 0;
        _weightHint.IsVisible = false;
        _stock.Text = "0";
        _minStock.Text = "0";

        if (_selectedArticleCategoryForList is not null)
        {
            _articleCategory.SelectedItem = _selectedArticleCategoryForList;
        }
        else
        {
            _articleCategory.SelectedIndex =
                _catalog.Categories.Count > 0
                    ? 0
                    : -1;
        }

        _selectedImage = "";
        _imageText.Text = "Kein Bild";
        SetImagePreview("");
        _variants.Clear();
        _comboItems.Clear();
        _comboChoiceGroup.Text = "";

        RefreshInheritedVat();
        RefreshComboSummary();
    }

    private void NewExtra()
    {
        _selectedExtra = null;
        _extraList.SelectedIndex = -1;
        _extraName.Text = "";
        _extraPrice.Text = "";
        _extraSort.Text = "0";
        _extraCategory.SelectedIndex =
            _catalog.Categories.Count > 0 ? 0 : -1;
        RefreshExtraVat();
    }

    private void OnGroupSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_groupList.SelectedItem
            is not GroupRow row)
        {
            return;
        }

        var group =
            _catalog.Groups.FirstOrDefault(
                x => x.Id == row.Id);

        if (group is null)
            return;

        _selectedGroup = group;
        _groupName.Text = group.Name;
        _groupSort.Text =
            group.SortOrder.ToString();
    }

    private void OnCategorySelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_categoryList.SelectedItem
            is not CategoryRow row)
        {
            return;
        }

        var category =
            _catalog.Categories.FirstOrDefault(
                x => x.Id == row.Id);

        if (category is null)
            return;

        _selectedCategory = category;

        _categoryName.Text =
            category.Name;

        _categorySort.Text =
            category.SortOrder.ToString();

        _categoryVat.SelectedItem =
            category.VatRate.ToString("0");

        _categoryImHausApplicable.IsChecked = category.ImHausApplicable;

        _categoryColor.Text = NormalizeCategoryColor(category.TileColor);

        _categoryKitchenStation.SelectedItem = KitchenStations.Normalize(category.KitchenStation);

        _categoryGroup.SelectedItem =
            _catalog.Groups.FirstOrDefault(
                x => x.Id == category.GroupId);
    }

    private async void OnArticleSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_articleList.SelectedItem
            is not ArticleRow row)
        {
            return;
        }

        var product =
            await _repo.GetByIdAsync(
                row.Id);

        if (product is null || _articleList.SelectedItem is not ArticleRow current || current.Id!=row.Id)
            return;

        _selectedArticle = product;

        _name.Text =
            product.Name;

        _barcode.Text =
            product.Barcode;

        _sku.Text =
            product.Sku;

        _price.Text =
            (product.BasePriceCents / 100m)
            .ToString(
                "0.00",
                System.Globalization.CultureInfo
                    .GetCultureInfo("de-DE"));

        _purchasePrice.Text =
            (product.PurchasePriceCents / 100m)
            .ToString(
                "0.00",
                System.Globalization.CultureInfo
                    .GetCultureInfo("de-DE"));

        _pfand.Text =
            (product.PfandCents / 100m)
            .ToString(
                "0.00",
                System.Globalization.CultureInfo
                    .GetCultureInfo("de-DE"));

        _soldByWeight.IsChecked = product.IsWeighted;
        _unit.SelectedItem = product.IsWeighted ? "kg" : product.Unit;
        ApplyWeightMode();

        _stock.Text = product.StockQuantity.ToString(
            "0.###",
            System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

        _minStock.Text = product.MinStockQuantity.ToString(
            "0.###",
            System.Globalization.CultureInfo.GetCultureInfo("de-DE"));

        var selectedCategory =
            _catalog.Categories
                .FirstOrDefault(
                    x => x.Id ==
                         product.CategoryId);

        _articleCategory.SelectedItem =
            selectedCategory;
        _selectedArticleCategoryForList = selectedCategory;
        _syncingArticleCategoryUi = true;
        try
        {
            _articleCategoryList.SelectedItem =
                (_articleCategoryList.ItemsSource as IEnumerable<CategoryRow>)?
                    .FirstOrDefault(x => x.Id == product.CategoryId);
        }
        finally
        {
            _syncingArticleCategoryUi = false;
        }

        _selectedImage =
            product.ImagePath;

        _imageText.Text =
            string.IsNullOrWhiteSpace(
                product.ImagePath)
                ? "Kein Bild"
                : Path.GetFileName(
                    product.ImagePath);
        SetImagePreview(product.ImagePath);

        _variants.Clear();

        foreach (var variant
                 in product.Variants)
        {
            _variants.Add(
                new VariantRow(
                    variant.Name,
                    variant.PriceCents));
        }
        _comboItems.Clear();
        _comboChoiceGroup.Text = "";
        foreach (var item in product.ComboItems)
        {
            var component = _catalog.Products.FirstOrDefault(x => x.Id == item.ComponentProductId);
            _comboItems.Add(new ComboRow(
                item.ComponentProductId,
                item.ComponentName,
                item.Quantity,
                component?.BasePriceCents ?? 0,
                component?.VatRate ?? 0m,
                component?.ImHausApplicable ?? true,
                item.ChoiceGroup));
        }

        RefreshInheritedVat();
        RefreshComboSummary();
    }

    private void RefreshInheritedVat()
    {
        if (_articleCategory.SelectedItem
            is Category category)
        {
            _articleVat.Text =
                $"{category.VatRate:0} % · automatisch aus Warengruppe „{category.Name}“";
        }
        else
        {
            _articleVat.Text =
                "Bitte Warengruppe auswählen";
        }
    }

    private void RefreshExtraVat()
    {
        _extraVat.Text = _extraCategory.SelectedItem is Category category
            ? $"{category.VatRate:0} % · aus Warengruppe „{category.Name}“"
            : "Bitte Warengruppe auswählen";
    }

    private void OnExtraSelected(
        object? sender,
        SelectionChangedEventArgs e)
    {
        if (_extraList.SelectedItem is not ExtraRow row)
            return;

        var extra = _extras.FirstOrDefault(x => x.Id == row.Id);
        if (extra is null)
            return;

        _selectedExtra = extra;
        _extraName.Text = extra.Name;
        _extraPrice.Text = (extra.PriceCents / 100m).ToString(
            "0.00",
            System.Globalization.CultureInfo.GetCultureInfo("de-DE"));
        _extraSort.Text = extra.SortOrder.ToString();
        _extraCategory.SelectedItem = _catalog.Categories.FirstOrDefault(
            x => x.Id == extra.CategoryId);
        RefreshExtraVat();
    }

    private async void SaveExtra(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_extraCategory.SelectedItem is not Category category)
                throw new InvalidOperationException("Bitte zuerst eine Warengruppe auswählen.");

            var name = (_extraName.Text ?? "").Trim();
            if (name.Length == 0)
                throw new InvalidOperationException("Bitte Extra-Name eingeben.");

            if (!Formatting.TryParseMoney(_extraPrice.Text, out var price))
                throw new InvalidOperationException("Extra-Preis ist ungültig.");

            var sort = int.TryParse(_extraSort.Text, out var parsed) ? parsed : 0;
            var id = await _repo.SaveExtraAsync(new ExtraItem(
                _selectedExtra?.Id ?? 0,
                category.Id,
                name,
                price,
                category.VatRate,
                sort));

            _extras = await _repo.GetExtrasAsync();
            _saved = true;
            RefreshExtraList();
            _selectedExtra = _extras.FirstOrDefault(x => x.Id == id);
            _extraList.SelectedItem =
                (_extraList.ItemsSource as IEnumerable<ExtraRow>)?
                .FirstOrDefault(x => x.Id == id);
            _extraVat.Text = $"Gespeichert · {category.VatRate:0} % aus Warengruppe";
        }
        catch (Exception ex)
        {
            _extraVat.Text = ex.Message.Contains("UNIQUE", StringComparison.OrdinalIgnoreCase)
                ? "Fehler: Extra-Name bereits vorhanden."
                : "Fehler: " + ex.Message;
        }
    }

    private async void SaveGroup(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            var name =
                (_groupName.Text ?? "").Trim();

            if (name.Length == 0)
                throw new InvalidOperationException(
                    "Bitte Gruppenname eingeben.");

            var sort =
                int.TryParse(
                    _groupSort.Text,
                    out var parsed)
                    ? parsed
                    : 0;

            var id =
                await _repo.SaveGroupAsync(
                    new ProductGroup(
                        _selectedGroup?.Id ?? 0,
                        name,
                        sort));

            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();

            _selectedGroup =
                _catalog.Groups.FirstOrDefault(
                    x => x.Id == id);

            _groupList.SelectedItem =
                (_groupList.ItemsSource
                    as IEnumerable<GroupRow>)?
                .FirstOrDefault(
                    x => x.Id == id);

            _groupName.Text = name;
        }
        catch (Exception ex)
        {
            _groupName.Text =
                "FEHLER: " + ex.Message;
        }
    }

    private async void MoveCategoryUp(object? sender, RoutedEventArgs e) => await MoveCategory(-1);

    private async void MoveCategoryDown(object? sender, RoutedEventArgs e) => await MoveCategory(1);

    // R64: verschiebt die gewählte Warengruppe in genau der Reihenfolge, in der
    // sie auch auf dem Kassenbildschirm erscheint (sort_order, dann Name).
    // Danach werden ALLE Warengruppen lückenlos neu durchnummeriert (0,1,2,...).
    // Das repariert nebenbei alte/doppelte Sortierwerte, ohne dass jemand die
    // Sortier-Nummer jeder einzelnen Warengruppe von Hand anfassen muss.
    private async Task MoveCategory(int direction)
    {
        if (_selectedCategory is null)
        {
            _categoryName.Text = "FEHLER: Bitte zuerst eine Warengruppe auswählen.";
            return;
        }

        var selectedId = _selectedCategory.Id;

        var ordered = _catalog.Categories
            .OrderBy(x => x.SortOrder)
            .ThenBy(x => x.Name)
            .ToList();

        var index = ordered.FindIndex(x => x.Id == selectedId);
        var targetIndex = index + direction;
        if (index < 0 || targetIndex < 0 || targetIndex >= ordered.Count)
            return;

        (ordered[index], ordered[targetIndex]) = (ordered[targetIndex], ordered[index]);

        try
        {
            // R64 FIX1: eine einzige DB-Transaktion; kein erneutes Schreiben von
            // Name, Gruppe, MwSt., Farbe oder Produkt-MwSt.-Propagation.
            await _repo.ReorderCategoriesAsync(
                ordered.Select(x => x.Id).ToArray());

            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();

            _selectedCategory = _catalog.Categories.FirstOrDefault(x => x.Id == selectedId);
            if (_selectedCategory is not null)
            {
                _categorySort.Text = _selectedCategory.SortOrder.ToString();
                _categoryList.SelectedItem =
                    (_categoryList.ItemsSource as IEnumerable<CategoryRow>)?
                        .FirstOrDefault(x => x.Id == selectedId);
            }
        }
        catch (Exception ex)
        {
            _categoryName.Text = "FEHLER: " + ex.Message;
        }
    }

    private async void SaveCategory(
        object? sender,
        RoutedEventArgs e)
    {
        try
        {
            if (_categoryGroup.SelectedItem
                is not ProductGroup group)
            {
                throw new InvalidOperationException(
                    "Bitte zuerst eine Gruppe auswählen.");
            }

            var name =
                (_categoryName.Text ?? "").Trim();

            if (name.Length == 0)
                throw new InvalidOperationException(
                    "Bitte Warengruppe eingeben.");

            if (!decimal.TryParse(
                _categoryVat.SelectedItem?
                    .ToString(),
                out var vat))
            {
                throw new InvalidOperationException(
                    "Bitte 7 % oder 19 % auswählen.");
            }

            var sort =
                int.TryParse(
                    _categorySort.Text,
                    out var parsed)
                    ? parsed
                    : 0;

            var color = NormalizeCategoryColor(_categoryColor.Text);
            _categoryColor.Text = color;

            var station = KitchenStations.Normalize(_categoryKitchenStation.SelectedItem as string);

            var id =
                await _repo.SaveCategoryAsync(
                    new Category(
                        _selectedCategory?.Id ?? 0,
                        group.Id,
                        name,
                        vat,
                        sort,
                        color,
                        station,
                        _categoryImHausApplicable.IsChecked ?? true));

            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();

            _selectedCategory =
                _catalog.Categories
                    .FirstOrDefault(
                        x => x.Id == id);

            _categoryList.SelectedItem =
                (_categoryList.ItemsSource
                    as IEnumerable<CategoryRow>)?
                .FirstOrDefault(
                    x => x.Id == id);

            RefreshArticleList();
        }
        catch (Exception ex)
        {
            _categoryName.Text =
                "FEHLER: " + ex.Message;
        }
    }

    private async void DeleteGroup(object? sender, RoutedEventArgs e)
    {
        if (_selectedGroup is null) { _groupName.Text = "FEHLER: Bitte zuerst eine Gruppe auswählen."; return; }
        if (!_user.IsAdmin) { _groupName.Text = "FEHLER: Löschen ist nur für Administratoren erlaubt."; return; }

        var categoryIds = _catalog.Categories.Where(x => x.GroupId == _selectedGroup.Id).Select(x => x.Id).ToHashSet();
        var articleCount = _catalog.Products.Count(x => categoryIds.Contains(x.CategoryId));
        var ok = await ConfirmDeleteAsync(
            "GRUPPE LÖSCHEN",
            $"Gruppe „{_selectedGroup.Name}“ wirklich löschen?\n\n{categoryIds.Count} Warengruppen und {articleCount} Artikel werden ausgeblendet. Historische Verkäufe bleiben unverändert erhalten.");
        if (!ok) return;

        try
        {
            await _repo.DeactivateGroupAsync(_selectedGroup.Id, _user.Username);
            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();
            NewGroup(); NewCategory(); NewArticle();
        }
        catch (Exception ex) { _groupName.Text = "FEHLER: " + ex.Message; }
    }

    private async void OpenCategoryPromotion(
        object? sender,
        RoutedEventArgs e)
    {
        if (_selectedCategory is null)
        {
            _imageText.Text =
                "ANGEBOT: Bitte zuerst eine Warengruppe auswählen.";
            return;
        }

        await new PromotionManagementWindow(
            _promotions,
            _user,
            PromotionScope.Category,
            categoryId: _selectedCategory.Id,
            categoryName: _selectedCategory.Name)
            .ShowDialog(this);
    }

    private async void OpenArticlePromotion(
        object? sender,
        RoutedEventArgs e)
    {
        if (_selectedArticle is null)
        {
            _imageText.Text =
                "ANGEBOT: Bitte zuerst einen Artikel auswählen.";
            return;
        }

        if (_selectedArticle.IsWeighted)
        {
            _imageText.Text =
                "ANGEBOT: Gewichtsartikel sind in R170 von Artikel-/Warengruppen-Angeboten ausgenommen, damit Teil-kg-Verkäufe centgenau bleiben. Normaler Bon-Rabatt bleibt möglich.";
            return;
        }

        var category =
            _catalog.Categories.FirstOrDefault(
                x => x.Id == _selectedArticle.CategoryId);

        await new PromotionManagementWindow(
            _promotions,
            _user,
            PromotionScope.Product,
            categoryId: category?.Id,
            categoryName: category?.Name ?? "",
            productId: _selectedArticle.Id,
            productName: _selectedArticle.Name)
            .ShowDialog(this);
    }

    private async void DeleteCategory(object? sender, RoutedEventArgs e)
    {
        if (_selectedCategory is null) { _categoryName.Text = "FEHLER: Bitte zuerst eine Warengruppe auswählen."; return; }
        if (!_user.IsAdmin) { _categoryName.Text = "FEHLER: Löschen ist nur für Administratoren erlaubt."; return; }

        var articleCount = _catalog.Products.Count(x => x.CategoryId == _selectedCategory.Id);
        var ok = await ConfirmDeleteAsync(
            "WARENGRUPPE LÖSCHEN",
            $"Warengruppe „{_selectedCategory.Name}“ wirklich löschen?\n\n{articleCount} zugeordnete Artikel werden ebenfalls ausgeblendet. Historische Verkäufe bleiben unverändert erhalten.");
        if (!ok) return;

        try
        {
            await _repo.DeactivateCategoryAsync(_selectedCategory.Id, _user.Username);
            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();
            NewCategory(); NewArticle();
        }
        catch (Exception ex) { _categoryName.Text = "FEHLER: " + ex.Message; }
    }

    private async void DeleteArticle(object? sender, RoutedEventArgs e)
    {
        if (_selectedArticle is null) { _imageText.Text = "FEHLER: Bitte zuerst einen Artikel auswählen."; return; }
        if (!_user.IsAdmin) { _imageText.Text = "FEHLER: Löschen ist nur für Administratoren erlaubt."; return; }

        var ok = await ConfirmDeleteAsync(
            "ARTIKEL LÖSCHEN",
            $"Artikel „{_selectedArticle.Name}“ (Art.-Nr. {_selectedArticle.Sku}) wirklich löschen?\n\nDer Artikel wird aus Kasse, Inventur und Cloud-Bestand ausgeblendet. Historische Verkäufe bleiben unverändert erhalten.");
        if (!ok) return;

        try
        {
            await _repo.DeactivateProductAsync(_selectedArticle.Id, _user.Username);
            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();
            _saved = true;
            RefreshAllBindings();
            NewArticle();
        }
        catch (Exception ex) { _imageText.Text = "FEHLER: " + ex.Message; }
    }

    private async Task<bool> ConfirmUnitRecountAsync(Product previous, string newUnit, decimal stock, decimal minStock)
    {
        var de = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
        var dialog = new Window
        {
            Title = "EINHEIT GEÄNDERT",
            Width = 640,
            Height = 380,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "NEU GEZÄHLT · SPEICHERN", MinWidth = 220, MinHeight = 48 };
        var no = new Button { Content = "ABBRECHEN", MinWidth = 150, MinHeight = 48 };
        yes.Click += (_,_) => dialog.Close(true);
        no.Click += (_,_) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = "EINHEIT GEÄNDERT", FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock
                {
                    Text = StockUnitRules.RecountMessage(previous.Unit, newUnit, previous.StockQuantity, previous.MinStockQuantity),
                    TextWrapping = TextWrapping.Wrap
                },
                new Border
                {
                    Background = Brush.Parse("#3A2A15"), CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(10),
                    Child = new TextBlock
                    {
                        Text = $"Wird gespeichert: Bestand {stock.ToString("0.###", de)} {newUnit} · Mindestbestand {minStock.ToString("0.###", de)} {newUnit}",
                        TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.WarningAmber
                    }
                },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { yes, no } }
            }
        };
        dialog.Opened += (_,_) => UiLanguage.Apply(dialog);
        return await dialog.ShowDialog<bool>(this);
    }

    private async Task<bool> ConfirmDeleteAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 600,
            Height = 330,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var yes = new Button { Content = "LÖSCHEN", MinWidth = 150, MinHeight = 48, Background = AppTheme.DangerRed, Foreground = Brushes.White };
        var no = new Button { Content = "ABBRECHEN", MinWidth = 150, MinHeight = 48 };
        yes.Click += (_,_) => dialog.Close(true);
        no.Click += (_,_) => dialog.Close(false);
        dialog.Content = new StackPanel
        {
            Margin = new Avalonia.Thickness(22),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.Bold },
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new Border
                {
                    Background = Brush.Parse("#3A2A15"), CornerRadius = new Avalonia.CornerRadius(8), Padding = new Avalonia.Thickness(10),
                    Child = new TextBlock { Text = "Löschen = deaktivieren. Fiskal-/Verkaufs-Historie wird nicht gelöscht.", TextWrapping = TextWrapping.Wrap, Foreground = AppTheme.WarningAmber }
                },
                new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10, Children = { yes, no } }
            }
        };
        dialog.Opened += (_,_) => UiLanguage.Apply(dialog);
        return await dialog.ShowDialog<bool>(this);
    }

    private static void StyleDangerButton(Button button)
    {
        button.Background = AppTheme.DangerRed;
        button.Foreground = Brushes.White;
        button.BorderBrush = Brush.Parse("#A64B5C");
        button.BorderThickness = new Avalonia.Thickness(1);
    }

    private void SetImagePreview(string? path)
    {
        _imagePreviewBitmap?.Dispose();
        _imagePreviewBitmap = null;
        _imagePreview.Source = null;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        try
        {
            using var stream = File.OpenRead(path);
            _imagePreviewBitmap = new Bitmap(stream);
            _imagePreview.Source = _imagePreviewBitmap;
        }
        catch { }
    }

    private async void ChooseImage(
        object? sender,
        RoutedEventArgs e)
    {
        var result =
            await StorageProvider
                .OpenFilePickerAsync(
                    new FilePickerOpenOptions
                    {
                        Title =
                            "Produktbild auswählen",
                        AllowMultiple = false,
                        FileTypeFilter =
                        [
                            new FilePickerFileType(
                                "Produktbilder")
                            {
                                Patterns =
                                [
                                    "*.jpg",
                                    "*.jpeg",
                                    "*.png",
                                    "*.webp",
                                    "*.bmp"
                                ]
                            }
                        ]
                    });

        var file =
            result.FirstOrDefault();

        if (file is null)
            return;

        _selectedImage =
            file.Path.LocalPath;

        _imageText.Text =
            Path.GetFileName(
                _selectedImage);
        SetImagePreview(_selectedImage);
    }

    private async void SaveArticle(
        object? sender,
        RoutedEventArgs e)
    {
        if(!IsEnabled)return;
        IsEnabled=false;
        try
        {
            if (_articleCategory.SelectedItem
                is not Category category)
            {
                throw new InvalidOperationException(
                    "Bitte zuerst eine Warengruppe auswählen.");
            }

            var name =
                (_name.Text ?? "").Trim();

            if (name.Length == 0)
                throw new InvalidOperationException(
                    "Bitte Artikelname eingeben.");

            if (!Formatting.TryParseMoney(
                _price.Text,
                out var price))
            {
                throw new InvalidOperationException(
                    "Verkaufspreis ist ungültig.");
            }

            long purchasePrice = 0;
            if (!string.IsNullOrWhiteSpace(_purchasePrice.Text) &&
                !Formatting.TryParseMoney(_purchasePrice.Text, out purchasePrice))
            {
                throw new InvalidOperationException("Einkaufspreis ist ungültig.");
            }

            var weighted = _soldByWeight.IsChecked == true ||
                           string.Equals(_unit.SelectedItem?.ToString(), "kg", StringComparison.OrdinalIgnoreCase);

            long pfand = 0;
            if (!weighted &&
                InstallationEdition.ReadLocked() == "KIOSK" &&
                !Formatting.TryParseMoney(_pfand.Text, out pfand))
            {
                pfand = 0;
            }

            if (weighted)
            {
                if (price <= 0)
                    throw new InvalidOperationException("Gewichtsartikel benötigen einen Verkaufspreis pro kg größer 0.");
                pfand = 0;
            }

            var stockRaw = (_stock.Text ?? "").Trim().Replace(',', '.');
            if (!decimal.TryParse(
                    stockRaw,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var stock) || stock < 0)
            {
                throw new InvalidOperationException("Bestand / Anzahl ist ungültig.");
            }
            var minStockRaw = (_minStock.Text ?? "").Trim().Replace(',', '.');
            if (!decimal.TryParse(
                    minStockRaw.Length == 0 ? "0" : minStockRaw,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var minStock) || minStock < 0)
            {
                throw new InvalidOperationException("Mindestbestand ist ungültig.");
            }

            var expectedStock = _selectedArticle?.StockQuantity ?? 0m;

            var imagePath =
                _selectedArticle?.ImagePath ?? "";

            if (!string.IsNullOrWhiteSpace(
                    _selectedImage) &&
                !string.Equals(
                    _selectedImage,
                    imagePath,
                    StringComparison.OrdinalIgnoreCase))
            {
                imagePath =
                    await _images.ImportAsync(
                        _selectedImage);
            }

            var product =
                new Product
                {
                    Id =
                        _selectedArticle?.Id ?? 0,
                    CategoryId =
                        category.Id,
                    Name =
                        name,
                    Barcode =
                        (_barcode.Text ?? "").Trim(),
                    Sku =
                        _selectedArticle?.Sku ?? "",
                    BasePriceCents =
                        price,

                    // UI shows inherited tax.
                    // Repository also enforces category VAT server-side.
                    VatRate =
                        category.VatRate,

                    PfandCents =
                        pfand,
                    Unit =
                        weighted
                            ? "kg"
                            : (_unit.SelectedItem?.ToString() ?? "Stück"),
                    ImagePath =
                        imagePath,
                    IsActive = true,
                    SortOrder =
                        _selectedArticle?
                            .SortOrder ?? 0,
                    MinStockQuantity = minStock,
                    PurchasePriceCents = Math.Max(0, purchasePrice)
                };

            var variants=_variants.Select((x,index)=>new ProductVariant(0,product.Id,x.Name,x.PriceCents,index)).ToArray();
            var comboItems=BuildComboItems(product.Id);

            if (weighted && (variants.Length > 0 || comboItems.Length > 0))
                throw new InvalidOperationException("Gewichtsartikel dürfen keine Varianten oder Menü-/Combo-Bestandteile haben.");

            if (comboItems.Length > 0)
            {
                var menuProduct = new Product
                {
                    Id = product.Id,
                    CategoryId = product.CategoryId,
                    Name = product.Name,
                    BasePriceCents = product.BasePriceCents,
                    VatRate = product.VatRate,
                    ComboItems = comboItems
                };

                ValidateChoiceGroups();
                var minMarket = MinimumMenuMarketValue();
                if (price > minMarket)
                    throw new InvalidOperationException(
                        $"Menüpreis {Formatting.Money(price)} darf die günstigste mögliche Einzelpreis-Summe {Formatting.Money(minMarket)} nicht übersteigen.");

                var selection = DefaultMenuSelection();
                var takeAway = MenuVatPolicy.Analyze(
                    menuProduct,_catalog.Products,imHaus:false,menuGrossCents:price,selectedComponents:selection);
                var inHouse = MenuVatPolicy.Analyze(
                    menuProduct,_catalog.Products,imHaus:true,menuGrossCents:price,selectedComponents:selection);
                if (!takeAway.IsValid)
                    throw new InvalidOperationException("Menü Außer Haus: " + takeAway.Message);
                if (!inHouse.IsValid)
                    throw new InvalidOperationException("Menü Im Haus: " + inHouse.Message);
            }

            // A unit change (Stück <-> kg, ...) on an article with a count
            // needs Bestand and Mindestbestand counted again in the new unit;
            // the numbers are never carried over silently (12 Stück != 12 kg).
            var unitRecount = _selectedArticle is { } previous &&
                StockUnitRules.NeedsRecount(previous.Unit, product.Unit, previous.StockQuantity, previous.MinStockQuantity);
            if (unitRecount &&
                !await ConfirmUnitRecountAsync(_selectedArticle!, product.Unit, stock, minStock))
            {
                _imageText.Text = UiLanguage.T("Nicht gespeichert: Bestand und Mindestbestand bitte in der neuen Einheit eintragen.");
                return;
            }

            var id = await _repo.SaveWithStockAsync(product,
                _selectedArticle is null || stock != expectedStock || unitRecount ? stock : null,
                expectedStock, _user.Username, variants:variants, comboItems:comboItems, unitChangeRecounted:unitRecount);

            await _catalog.ReloadAsync();
            App.CloudSync?.RequestStockRefresh();

            _saved = true;
            RefreshAllBindings();

            _selectedArticle =
                _catalog.Products.FirstOrDefault(
                    x => x.Id == id);

            RefreshArticleList();

            _articleList.SelectedItem =
                (_articleList.ItemsSource
                    as IEnumerable<ArticleRow>)?
                .FirstOrDefault(
                    x => x.Id == id);

            _imageText.Text = weighted
                ? $"Gespeichert · Art.-Nr. {_selectedArticle?.Sku ?? "–"} · GEWICHTSARTIKEL · {Formatting.Money(price)}/kg · Bestand {stock:0.###} kg · Mindestbestand {minStock:0.###} kg · MwSt. {category.VatRate:0} %"
                : $"Gespeichert · Art.-Nr. {_selectedArticle?.Sku ?? "–"} · Bestand {stock:0.###} {_unit.SelectedItem?.ToString() ?? "Stück"} · Mindestbestand {minStock:0.###} · MwSt. {category.VatRate:0} %";
        }
        catch (Exception ex)
        {
            _imageText.Text =
                ex.Message.Contains(
                    "UNIQUE",
                    StringComparison.OrdinalIgnoreCase)
                    ? "Fehler: Barcode oder Name bereits vergeben."
                    : "Fehler: " + ex.Message;
        }
        finally { IsEnabled=true; }
    }

    private async void AddVariant(
        object? sender,
        RoutedEventArgs e)
    {
        var variant =
            await new VariantEditWindow()
                .ShowDialog<ProductVariant?>(
                    this);

        if (variant is not null)
        {
            _variants.Add(
                new VariantRow(
                    variant.Name,
                    variant.PriceCents));
        }
    }

    private async void EditVariant(
        object? sender,
        RoutedEventArgs e)
    {
        var index =
            _variantList.SelectedIndex;

        if (index < 0)
            return;

        var current =
            _variants[index];

        var variant =
            await new VariantEditWindow(
                new ProductVariant(
                    0,
                    0,
                    current.Name,
                    current.PriceCents))
                .ShowDialog<ProductVariant?>(
                    this);

        if (variant is not null)
        {
            _variants[index] =
                new VariantRow(
                    variant.Name,
                    variant.PriceCents);
        }
    }

    private sealed record GroupRow(
        long Id,
        string Name,
        int SortOrder)
    {
        public override string ToString() =>
            $"{SortOrder:000}   {Name}";
    }

    private sealed record CategoryRow(
        long Id,
        string Group,
        string Name,
        decimal VatRate,
        int SortOrder)
    {
        public override string ToString() =>
            $"{Group}  ›  {Name}   ·   {VatRate:0} %";
    }

    private sealed record ArticleRow(
        long Id,
        string Group,
        string Category,
        string Name,
        string Barcode,
        string Sku,
        decimal StockQuantity,
        decimal MinStockQuantity,
        long TotalPriceCents,
        decimal VatRate)
    {
        public override string ToString() =>
            $"{(MinStockQuantity > 0m && StockQuantity <= MinStockQuantity ? "⚠ " : "")}{Name} · WG {Category} · Art.-Nr. {Sku} · Bestand {StockQuantity:0.###}" +
            (MinStockQuantity > 0m ? $" / Min. {MinStockQuantity:0.###}" : "") +
            $" · {Formatting.Money(TotalPriceCents)}" +
            (string.IsNullOrWhiteSpace(Barcode) ? "" : $" · EAN {Barcode}");
    }

    private sealed record ExtraRow(
        long Id,
        string Category,
        string Name,
        long PriceCents,
        decimal VatRate,
        int SortOrder)
    {
        public override string ToString() =>
            $"{Category}  ›  {Name}   {Formatting.Money(PriceCents)} · {VatRate:0} %";
    }

    private sealed record ComboRow(
        long ComponentProductId,
        string Name,
        decimal Quantity,
        long NormalPriceCents,
        decimal VatRate,
        bool ImHausApplicable,
        string ChoiceGroup)
    {
        public override string ToString() =>
            $"{(string.IsNullOrWhiteSpace(ChoiceGroup) ? "[FEST]" : $"[{ChoiceGroup}]")} " +
            $"{Quantity:0.##} x {Name} · Einzelpreis {Formatting.Money(NormalPriceCents)} · MwSt. {VatRate:0}%";
    }

    private sealed record VariantRow(
        string Name,
        long PriceCents)
    {
        public override string ToString() =>
            $"{Name}   {Formatting.Money(PriceCents)}";
    }
}


public sealed record ComboComponentResult(long ProductId, decimal Quantity);
public sealed record ComboProductChoice(long Id,string Name,long PriceCents,decimal VatRate)
{
    public override string ToString() =>
        $"{Name} · {Formatting.Money(PriceCents)} · MwSt. {VatRate:0}%";
}

public sealed class ComboComponentWindow : Window
{
    private readonly ComboBox _product=new(){MinWidth=380};
    private readonly TextBox _qty=new(){Text="1",MinWidth=120};
    public ComboComponentWindow(IReadOnlyList<Product> products)
    {
        Opened += (_, _) => UiLanguage.Apply(this);
        Title="Menü-Bestandteil";Width=620;Height=300;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        _product.ItemsSource=products.Select(x=>new ComboProductChoice(x.Id,x.Name,x.BasePriceCents,x.VatRate)).ToArray();_product.SelectedIndex=products.Count>0?0:-1;
        var ok=new Button{Content="ÜBERNEHMEN",MinHeight=46,MinWidth=160};var cancel=new Button{Content="ABBRECHEN",MinHeight=46,MinWidth=140};
        ok.Click+=(_,_)=>{if(_product.SelectedItem is not ComboProductChoice p)return;var raw=(_qty.Text??"").Replace(',','.');if(!decimal.TryParse(raw,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var q)||q<=0)return;Close(new ComboComponentResult(p.Id,q));};cancel.Click+=(_,_)=>Close(null);
        Content=new StackPanel{Margin=new Avalonia.Thickness(20),Spacing=12,Children={new TextBlock{Text="ARTIKEL FÜR MENÜ WÄHLEN",FontSize=22,FontWeight=FontWeight.Bold},new TextBlock{Text="Bestandteil",Opacity=.7},_product,new TextBlock{Text="Menge",Opacity=.7},_qty,new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=10,Children={ok,cancel}}}};
    }
}
public sealed class ComboQuantityWindow : Window
{
    private readonly TextBox _qty=new(){MinWidth=140};
    public ComboQuantityWindow(string name,decimal quantity)
    {
        Opened += (_, _) => UiLanguage.Apply(this);
        Title="Menü-Menge";Width=480;Height=240;WindowStartupLocation=WindowStartupLocation.CenterOwner;_qty.Text=quantity.ToString("0.##");
        var ok=new Button{Content="ÜBERNEHMEN",MinHeight=46,MinWidth=150};var cancel=new Button{Content="ABBRECHEN",MinHeight=46,MinWidth=130};
        ok.Click+=(_,_)=>{var raw=(_qty.Text??"").Replace(',','.');if(decimal.TryParse(raw,System.Globalization.NumberStyles.Number,System.Globalization.CultureInfo.InvariantCulture,out var q)&&q>0)Close(q);};cancel.Click+=(_,_)=>Close(null);
        Content=new StackPanel{Margin=new Avalonia.Thickness(20),Spacing=12,Children={new TextBlock{Text=name,FontSize=20,FontWeight=FontWeight.Bold},new TextBlock{Text="Menge im Menü"},_qty,new StackPanel{Orientation=Avalonia.Layout.Orientation.Horizontal,Spacing=10,Children={ok,cancel}}}};
    }
}
