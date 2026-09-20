using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TorPos.Core;

namespace TorPos.App;

public sealed class UserManagementWindow : Window
{
    private readonly IAuthenticationService _authentication;
    private readonly AuthenticatedUser _admin;
    private readonly TabControl _tabs = new();
    private readonly TextBlock _status = new();
    private readonly List<UserEditor> _editors = new();
    // R164: rebuilt with every tab reload so Avalonia never sees one TextBox
    // attached to both the old and the new admin StackPanel.
    private TextBox _adminCurrentPassword = null!;
    private TextBox _adminNewPassword = null!;
    private TextBox _adminNewPin = null!;

    public UserManagementWindow(
        IAuthenticationService authentication,
        AuthenticatedUser admin)
    {
        _authentication = authentication;
        _admin = admin;

        Title = "TOR POS – Mitarbeiter & Rechte";
        Width = 980;
        Height = 760;
        MinWidth = 820;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var save = new Button
        {
            Content = "ALLE BENUTZER SPEICHERN",
            MinWidth = 230,
            MinHeight = 50,
            FontWeight = FontWeight.Bold,
            Background = Brush.Parse("#2CC4A7"),
            Foreground = Brush.Parse("#07140F")
        };
        save.Click += async (_,_) => await SaveAsync();

        var close = new Button
        {
            Content = "SCHLIESSEN",
            MinWidth = 140,
            MinHeight = 50
        };
        close.Click += (_,_) => Close(true);

        Content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            Margin = new Avalonia.Thickness(22),
            RowSpacing = 14,
            Children =
            {
                new StackPanel
                {
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "MITARBEITER & RECHTE",
                            FontSize = 27,
                            FontWeight = FontWeight.Bold
                        },
                        new TextBlock
                        {
                            Text = "Neben dem Admin stehen genau drei Mitarbeiterkonten bereit. " +
                                   "Jedes Konto erhält eigene Zugangsdaten und Funktionsrechte.",
                            TextWrapping = TextWrapping.Wrap,
                            Opacity = 0.68
                        }
                    }
                },
                _tabs,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"),
                    ColumnSpacing = 10,
                    Children = { _status, close, save }
                }
            }
        };

        Grid.SetRow(_tabs, 1);
        if (Content is Grid grid)
        {
            var footer = (Grid)grid.Children[2];
            Grid.SetRow(footer, 2);
            Grid.SetColumn(close, 1);
            Grid.SetColumn(save, 2);
            _status.VerticalAlignment = VerticalAlignment.Center;
            _status.Text = "Benutzer werden geladen ...";
            _status.Opacity = 0.75;
        }

        Opened += async (_,_) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            var users = await _authentication.GetStaffUsersAsync();
            _tabs.Items.Clear();
            _editors.Clear();

            _tabs.Items.Add(new TabItem
            {
                Header = "ADMIN-ZUGANG",
                Content = BuildAdminCredentialPanel()
            });

            foreach (var user in users)
            {
                var editor = new UserEditor(user);
                _editors.Add(editor);
                _tabs.Items.Add(new TabItem
                {
                    Header = $"BENUTZER {user.Slot}",
                    Content = editor.Content
                });
            }

            _tabs.SelectedIndex = 0;
            _status.Text = users.Count == 3
                ? "3 Mitarbeiterkonten geladen."
                : $"Achtung: {users.Count} Mitarbeiterkonten gefunden.";
        }
        catch (Exception ex)
        {
            _status.Text = "Laden fehlgeschlagen: " + ex.Message;
        }
    }

    private Control BuildAdminCredentialPanel()
    {
        _adminCurrentPassword = new TextBox { PasswordChar = '●' };
        _adminNewPassword = new TextBox { PasswordChar = '●' };
        _adminNewPin = new TextBox { PasswordChar = '●', MaxLength = 4 };

        var change = new Button
        {
            Content = "ADMIN-ZUGANG ÄNDERN",
            MinHeight = 48,
            MinWidth = 220,
            FontWeight = FontWeight.Bold
        };

        change.Click += async (_,_) =>
        {
            try
            {
                var newPassword = _adminNewPassword.Text ?? "";
                var newPin = _adminNewPin.Text ?? "";

                if (newPassword.Length < 4)
                    throw new InvalidOperationException("Das neue Admin-Passwort muss mindestens 4 Zeichen haben.");

                await _authentication.ChangeAdminCredentialsAsync(
                    _adminCurrentPassword.Text ?? "",
                    newPassword,
                    newPin);

                _adminCurrentPassword.Text = "";
                _adminNewPassword.Text = "";
                _adminNewPin.Text = "";
                _status.Text = "Admin-Passwort und PIN wurden geändert.";
            }
            catch (Exception ex)
            {
                _status.Text = "Admin-Zugang konnte nicht geändert werden: " + ex.Message;
            }
        };

        return new ScrollViewer
        {
            VerticalScrollBarVisibility =
                Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(18),
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "ADMIN-ZUGANG",
                        FontSize = 22,
                        FontWeight = FontWeight.Bold
                    },
                    new TextBlock
                    {
                        Text = "Standard bei einer neuen Installation: Benutzer admin · Passwort admin · PIN 1234. " +
                               "Der Kunde kann Passwort und PIN hier jederzeit ändern.",
                        TextWrapping = TextWrapping.Wrap,
                        Opacity = 0.68
                    },
                    new TextBlock { Text = "Aktuelles Admin-Passwort", Opacity = 0.72 },
                    _adminCurrentPassword,
                    new TextBlock { Text = "Neues Admin-Passwort", Opacity = 0.72 },
                    _adminNewPassword,
                    new TextBlock { Text = "Neue 4-stellige Admin-PIN", Opacity = 0.72 },
                    _adminNewPin,
                    change
                }
            }
        };
    }

    private async Task SaveAsync()
    {
        try
        {
            foreach (var editor in _editors)
            {
                await _authentication.SaveStaffUserAsync(
                    editor.CreateUpdate(),
                    _admin.Username);
            }

            _status.Text = "Alle drei Mitarbeiterkonten wurden gespeichert.";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            _status.Text = "Speichern fehlgeschlagen: " + ex.Message;
        }
    }

    private sealed class UserEditor
    {
        private readonly StaffUser _user;
        private readonly TextBox _username = new();
        private readonly TextBox _password = new() { PasswordChar = '●' };
        private readonly TextBox _pin = new() { PasswordChar = '●', MaxLength = 4 };
        private readonly CheckBox _active = new() { Content = "Benutzer aktiv" };
        private readonly Dictionary<UserPermissions,CheckBox> _permissions = new();

        public UserEditor(StaffUser user)
        {
            _user = user;
            _username.Text = user.Username;
            _active.IsChecked = user.IsActive;

            var fields = new StackPanel { Spacing = 8 };
            AddField(fields, "Benutzername", _username);
            AddField(fields, "Neues Passwort", _password,
                user.CredentialsConfigured
                    ? "Leer lassen = vorhandenes Passwort behalten."
                    : "Zum Aktivieren reicht Passwort oder PIN. Passwort: mindestens 4 Zeichen.");
            AddField(fields, "Neue 4-stellige PIN", _pin,
                user.CredentialsConfigured
                    ? "Leer lassen = vorhandene PIN behalten."
                    : "Optional: 4 Ziffern. Leer lassen = Anmeldung nur per Passwort.");
            fields.Children.Add(_active);

            var permissionPanel = new WrapPanel
            {
                Orientation = Orientation.Horizontal
            };

            AddPermission(permissionPanel, UserPermissions.Sale, "Verkaufen / kassieren");
            AddPermission(permissionPanel, UserPermissions.Discount, "Rabatt");
            AddPermission(permissionPanel, UserPermissions.ImmediateStorno, "Sofort-Storno");
            AddPermission(permissionPanel, UserPermissions.ReceiptStorno, "Bon-Storno");
            AddPermission(permissionPanel, UserPermissions.ParkReceipts, "Bon parken / holen");
            AddPermission(permissionPanel, UserPermissions.CashMovement, "Einlage / Entnahme");
            AddPermission(permissionPanel, UserPermissions.ZReport, "Z-Bericht");
            AddPermission(permissionPanel, UserPermissions.ManageProducts, "Stammdaten");
            AddPermission(permissionPanel, UserPermissions.ViewReceiptHistory, "Bon-Historie");
            AddPermission(permissionPanel, UserPermissions.Training, "Training");

            Content = new ScrollViewer
            {
                VerticalScrollBarVisibility =
                    Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                Content = new StackPanel
                {
                    Margin = new Avalonia.Thickness(18),
                    Spacing = 14,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = $"Mitarbeiterkonto {user.Slot}",
                            FontSize = 22,
                            FontWeight = FontWeight.Bold
                        },
                        fields,
                        new TextBlock
                        {
                            Text = "Berechtigungen",
                            FontSize = 18,
                            FontWeight = FontWeight.Bold
                        },
                        permissionPanel,
                        new TextBlock
                        {
                            Text = "Einstellungen und Benutzerverwaltung bleiben immer ausschließlich beim Admin.",
                            TextWrapping = TextWrapping.Wrap,
                            Foreground = Brushes.Orange
                        }
                    }
                }
            };
        }

        public Control Content { get; }

        public StaffUserUpdate CreateUpdate()
        {
            var permissions = UserPermissions.None;
            foreach (var pair in _permissions)
            {
                if (pair.Value.IsChecked == true)
                    permissions |= pair.Key;
            }

            return new StaffUserUpdate(
                _user.Id,
                _username.Text ?? "",
                _active.IsChecked == true,
                permissions,
                _password.Text ?? "",
                _pin.Text ?? "");
        }

        private void AddPermission(
            Panel panel,
            UserPermissions permission,
            string label)
        {
            var check = new CheckBox
            {
                Content = label,
                IsChecked = (_user.Permissions & permission) == permission,
                Width = 245,
                Margin = new Avalonia.Thickness(4,6)
            };
            _permissions[permission] = check;
            panel.Children.Add(check);
        }

        private static void AddField(
            Panel panel,
            string label,
            Control control,
            string hint = "")
        {
            panel.Children.Add(new TextBlock { Text = label, Opacity = 0.72 });
            panel.Children.Add(control);
            if (hint.Length > 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = hint,
                    TextWrapping = TextWrapping.Wrap,
                    FontSize = 11,
                    Opacity = 0.55
                });
            }
        }
    }
}
