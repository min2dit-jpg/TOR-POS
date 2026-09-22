using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;
namespace TorPos.App;

internal sealed record CheckoutReviewResult(bool Paid, string Administrator, string Evidence);
internal sealed class CheckoutReviewWindow : Window
{
    public CheckoutReviewWindow(CheckoutOperation operation, IAuthenticationService auth)
    {
        Title="ZAHLUNG PRÜFEN"; Width=620; Height=580; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var user=new TextBox{Text="admin",PlaceholderText="Administrator"};
        var password=new TextBox{PasswordChar='●',PlaceholderText="Admin-Passwort"};
        var evidence=new TextBox{PlaceholderText="Terminalbeleg / Trace / Prüfnachweis und Begründung",AcceptsReturn=true,Height=90};
        var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var paid=new Button{Content="Zahlung bestätigt / Buchung abschließen",IsEnabled=operation.State!="PREPARED"};
        var noCharge=new Button{Content="Keine Belastung nachweislich bestätigt",IsEnabled=operation.State!="APPROVED"};
        async Task Resolve(bool isPaid)
        {
            paid.IsEnabled=false; noCharge.IsEnabled=false;
            try
            {
                var login=await auth.LoginWithPasswordAsync(user.Text??"",password.Text??"");
                if(!login.Success || login.User?.IsAdmin!=true || login.User.MustChangePassword)
                    throw new InvalidOperationException("Gültiger eingerichteter Administrator-Zugang erforderlich.");
                if((evidence.Text??"").Trim().Length<8) throw new InvalidOperationException("Prüfnachweis eingeben (mindestens 8 Zeichen).");
                Close(new CheckoutReviewResult(isPaid,login.User.Username,evidence.Text!.Trim()));
            }
            catch(Exception ex) { status.Text=ex.Message; paid.IsEnabled=operation.State!="PREPARED"; noCharge.IsEnabled=operation.State!="APPROVED"; }
        }
        paid.Click+=async (_,_)=>await Resolve(true);
        noCharge.Click+=async (_,_)=>await Resolve(false);
        var cancel=new Button{Content="Später prüfen · gesperrt lassen"}; cancel.Click+=(_,_)=>Close();
        Content=new StackPanel{Margin=new Thickness(22),Spacing=12,Children={
            new TextBlock{Text=$"Betrag: {Formatting.Money(operation.Snapshot.TotalCents)} · {operation.Snapshot.Method}\nCheckout-Status: {operation.State}\nTerminalergebnis: {PaymentOutcomeCodec.ToStorage(operation.TerminalOutcome)}\nZahlungsauftrag gesendet: {(operation.TerminalRequestSubmitted ? "JA" : "NEIN")}\nTerminal-Code: {operation.TerminalCode}\nAuflösung: {PaymentOutcomeCodec.ToStorage(operation.Resolution)}\nVorgang: {operation.Snapshot.OperationId}\n{operation.Evidence}",TextWrapping=Avalonia.Media.TextWrapping.Wrap},
            new TextBlock{Text="Zuerst Terminal / Netzbetreiber prüfen. Diese Auswahl sendet weder Zahlung noch Storno an das Terminal.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},
            user,password,evidence,paid,noCharge,cancel,status}};
    }
}

internal sealed class RequiredAdminCredentialsWindow : Window
{
    public RequiredAdminCredentialsWindow(IAuthenticationService auth)
    {
        Title="ADMIN-ZUGANG EINRICHTEN"; Width=500; Height=440; CanResize=false;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var oldPassword=new TextBox{PasswordChar='●',PlaceholderText="Aktuelles Passwort"};
        var password=new TextBox{PasswordChar='●',PlaceholderText="Neues Passwort (mindestens 4 Zeichen)"};
        var confirm=new TextBox{PasswordChar='●',PlaceholderText="Neues Passwort wiederholen"};
        var pin=new TextBox{PasswordChar='●',PlaceholderText="Neue PIN (4 Ziffern)"};
        var status=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};
        var save=new Button{Content="Zugang sicher speichern"};
        save.Click+=async (_,_)=>
        {
            save.IsEnabled=false;
            try
            {
                if(password.Text!=confirm.Text) throw new InvalidOperationException("Passwörter stimmen nicht überein.");
                await auth.ChangeAdminCredentialsAsync(oldPassword.Text??"",password.Text??"",pin.Text??"");
                Close(true);
            }
            catch(Exception ex){status.Text=ex.Message;save.IsEnabled=true;}
        };
        Content=new StackPanel{Margin=new Thickness(22),Spacing=14,Children={
            new TextBlock{Text="Vor dem ersten Kassenstart müssen Standard-Zugangsdaten geändert werden.",TextWrapping=Avalonia.Media.TextWrapping.Wrap},oldPassword,password,confirm,pin,save,status}};
    }
}
