using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using TorPos.Core;

namespace TorPos.App;

public partial class LoginWindow : Window
{
    private readonly IAuthenticationService _auth;
    private readonly ISettingsRepository _settings;
    private readonly string? _lockedEdition;

    public event Func<AuthenticatedUser, Task>? LoginSucceeded;
    private bool _busy;
    public event Action? ExitRequested;

    public LoginWindow(
        IAuthenticationService auth,
        ISettingsRepository settings,
        string? lockedEdition = null)
    {
        InitializeComponent();
        _auth = auth;
        _lockedEdition = NormalizeLockedEdition(lockedEdition);
        // Programmsprache wird unter Einstellungen verwaltet, nicht bei der
        // Anmeldung - gelesen wird hier nur der Trainingszugang (R122).
        _settings = settings;

        // R181: licence-free test installations may still switch editions so both
        // profiles can be tested independently. Once a commercial licence has
        // bound this installation, the other edition is not merely disabled -
        // it is hidden from the login UI and cannot be selected programmatically.
        if (_lockedEdition == "KIOSK")
        {
            ShowFixedEdition(KioskEditionRadio, ImbissEditionRadio);
            EditionStatusText.Text = UiLanguage.T("Kassenart: EINZELHANDEL · Lizenz/Installation fest gebunden.");
        }
        else if (_lockedEdition == "IMBISS")
        {
            ShowFixedEdition(ImbissEditionRadio, KioskEditionRadio);
            EditionStatusText.Text = UiLanguage.T("Kassenart: GASTRONOMIE · Lizenz/Installation fest gebunden.");
        }
        else if (_lockedEdition == "RESTAURANT")
        {
            KioskEditionRadio.Content = "RESTAURANT";
            ShowFixedEdition(KioskEditionRadio, ImbissEditionRadio);
            EditionStatusText.Text = "Kassenart: RESTAURANT · Lizenz/Installation fest gebunden.";
            Title = ProductBuild.ProductName + " – Anmeldung";
        }
        else
        {
            KioskEditionRadio.IsChecked = false;
            ImbissEditionRadio.IsChecked = false;
            KioskEditionRadio.IsVisible = true;
            ImbissEditionRadio.IsVisible = true;
            EditionStatusText.Text = UiLanguage.T("TEST · Einzelhandel oder Gastronomie auswählen. Betriebsdaten bleiben getrennt.");
        }

        Opened += async (_,_) =>
        {
            UiLanguage.Apply(this);
            PasswordBox.Focus();
            await RefreshTrainingHintAsync();
        };
    }

    internal static string? NormalizeLockedEdition(string? lockedEdition) =>
        string.Equals(lockedEdition, "KIOSK", StringComparison.OrdinalIgnoreCase) ? "KIOSK" :
        string.Equals(lockedEdition, "IMBISS", StringComparison.OrdinalIgnoreCase) ? "IMBISS" :
        string.Equals(lockedEdition, "RESTAURANT", StringComparison.OrdinalIgnoreCase) ? "RESTAURANT" :
        null;

    // R182: with a fixed Kassenart the remaining entry must not stay in its half
    // of the two-column selector - it looked pushed to one side. It spans the
    // whole row, sits centred and is shown at the size an operator reads from a
    // step away.
    private static void ShowFixedEdition(RadioButton visible, RadioButton hidden)
    {
        visible.IsChecked = true;
        visible.IsVisible = true;
        hidden.IsVisible = false;

        Grid.SetColumn(visible, 0);
        Grid.SetColumnSpan(visible, 2);
        visible.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        visible.HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center;
        visible.FontSize = 22;
        visible.MinHeight = 52;
    }

    public string? SelectedEdition =>
        _lockedEdition ??
        (KioskEditionRadio.IsChecked == true
            ? "KIOSK"
            : ImbissEditionRadio.IsChecked == true
                ? "IMBISS"
                : null);


    private void OnExitProgramClick(object? sender, RoutedEventArgs e)
    {
        LoginButton.IsEnabled = false;
        ExitProgramButton.IsEnabled = false;
        StatusText.Text = UiLanguage.T("Programm wird beendet · Datensicherung wird erstellt ...");
        ExitRequested?.Invoke();
    }

    private async void OnLoginClick(object? sender, RoutedEventArgs e)
    {
        if (TrainingModeBox.IsChecked == true)
            await TrainingLoginAsync();
        else
            await PasswordLoginAsync();
    }

    private async void OnTrainingCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await TrainingLoginAsync();
    }

    private async void OnPasswordKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (TrainingModeBox.IsChecked == true)
            await TrainingLoginAsync();
        else
            await PasswordLoginAsync();
    }


    private async Task PasswordLoginAsync()
    {
        if (_busy) return;
        if (SelectedEdition is null)
        {
            StatusText.Text = UiLanguage.T("Bitte zuerst Einzelhandel oder Gastronomie auswählen.");
            return;
        }

        SetBusy(true);
        try
        {
            var result = await _auth.LoginWithPasswordAsync(
                UsernameBox.Text ?? "",
                PasswordBox.Text ?? "");

            if (!result.Success || result.User is null)
            {
                StatusText.Text = UiLanguage.T(result.Message);
                PasswordBox.Text = "";
                PasswordBox.Focus();
                return;
            }

            if (LoginSucceeded is not null) await LoginSucceeded(result.User with {IsTraining=false});
        }
        catch(Exception ex) { CrashLog.WriteException("Login failed",ex); StatusText.Text=UiLanguage.T("Anmeldung fehlgeschlagen")+": "+ex.Message; }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary>
    /// R122: reads the configured training code. A settings failure must never
    /// stop someone reaching the login screen, so it falls back to the factory
    /// code - the same value this build used unconditionally before.
    /// </summary>
    private async Task<string> ReadTrainingCodeAsync()
    {
        try
        {
            return await _settings.GetAsync(
                TrainingAccessPolicy.SettingKey,
                TrainingAccessPolicy.FactoryCode);
        }
        catch (Exception ex)
        {
            CrashLog.WriteException("Training access code could not be read", ex);
            return TrainingAccessPolicy.FactoryCode;
        }
    }

    private async Task RefreshTrainingHintAsync()
    {
        var configured = await ReadTrainingCodeAsync();
        TrainingHintText.Text = UiLanguage.T(
            TrainingAccessPolicy.IsFactoryDefault(configured)
                ? "Training-Anmeldung nur mit Code 0000 · Benutzer-Passwort ist dann nicht erforderlich."
                : "Training-Anmeldung nur mit dem vom Betreiber gesetzten Training-Code · Benutzer-Passwort ist dann nicht erforderlich.");
    }

    private async Task TrainingLoginAsync()
    {
        if (_busy) return;
        if (SelectedEdition is null)
        {
            StatusText.Text = UiLanguage.T("Bitte zuerst Einzelhandel oder Gastronomie auswählen.");
            return;
        }

        var configuredTrainingCode = await ReadTrainingCodeAsync();
        if (!TrainingAccessPolicy.Matches(configuredTrainingCode, TrainingCodeBox.Text))
        {
            StatusText.Text = UiLanguage.T(
                TrainingAccessPolicy.IsFactoryDefault(configuredTrainingCode)
                    ? "Training-Code ist falsch. Standard-Code: 0000."
                    : "Training-Code ist falsch.");
            TrainingCodeBox.Text = "";
            TrainingCodeBox.Focus();
            return;
        }

        SetBusy(true);
        try
        {
            // Training uses its own local session and never needs the normal
            // user password. It has only the permissions needed for simulated sales.
            var trainingUser = new AuthenticatedUser(
                Id: 0,
                Username: "training",
                Role: "TRAINING",
                IsAdmin: false,
                MustChangePassword: false,
                Permissions:
                    UserPermissions.Sale |
                    UserPermissions.Discount |
                    UserPermissions.ImmediateStorno |
                    UserPermissions.Training,
                IsTraining: true);

            StatusText.Text = UiLanguage.T("TRAININGSMODUS wird geöffnet ...");
            if(LoginSucceeded is not null) await LoginSucceeded(trainingUser);
        }
        catch(Exception ex) { CrashLog.WriteException("Login failed",ex); StatusText.Text=UiLanguage.T("Anmeldung fehlgeschlagen")+": "+ex.Message; }
        finally
        {
            SetBusy(false);
        }

        await Task.CompletedTask;
    }

    private void SetBusy(bool busy)
    {
        _busy=busy;
        ExitProgramButton.IsEnabled=!busy;
        UsernameBox.IsEnabled = !busy;
        PasswordBox.IsEnabled = !busy;
        TrainingCodeBox.IsEnabled = !busy;
        TrainingModeBox.IsEnabled = !busy;
        LoginButton.IsEnabled = !busy;

        KioskEditionRadio.IsEnabled = !busy && _lockedEdition is null;
        ImbissEditionRadio.IsEnabled = !busy && _lockedEdition is null;
        StatusText.Text = busy ? UiLanguage.T("Anmeldung wird geprüft ...") : StatusText.Text;
    }

}
