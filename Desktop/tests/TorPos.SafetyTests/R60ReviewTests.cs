using TorPos.Infrastructure;

public static class R60ReviewTests
{
    public static Task Run(Action<bool,string> check)
    {
        check(
            ReportEmailService.NormalizeAppPassword("abcd efgh ijkl mnop") == "abcdefghijklmnop",
            "R60 Gmail App-Passwort removes spaces before SMTP authentication");

        check(
            ReportEmailService.NormalizeAppPassword("abcd\tefgh\nijkl mnop") == "abcdefghijklmnop",
            "R60 SMTP password normalization removes all whitespace");

        check(
            ReportEmailService.LooksMaskedPassword("••••••••") && ReportEmailService.LooksMaskedPassword("********") && !ReportEmailService.LooksMaskedPassword("abcd1234"),
            "R60 masked password placeholders are never accepted as real credentials");

        var protectedPassword = TorSecretProtector.Protect("abcd efgh ijkl mnop");
        check(
            ReportEmailService.ResolveAppPassword("", protectedPassword) == "abcdefghijklmnop",
            "R60 test mail can reuse and decrypt the stored App-Passwort");

        var gmailValid = true;
        try
        {
            ReportEmailService.Validate(new ReportEmailService.MailConfig(
                "kunde@example.com", "kasse@gmail.com", "smtp.gmail.com", 587, true,
                "kasse@gmail.com", "abcdefghijklmnop"));
        }
        catch { gmailValid = false; }
        check(gmailValid, "R60 Gmail preset validates smtp.gmail.com / 587 / STARTTLS");

        var gmailTlsRejected = false;
        try
        {
            ReportEmailService.Validate(new ReportEmailService.MailConfig(
                "kunde@example.com", "kasse@gmail.com", "smtp.gmail.com", 587, false,
                "kasse@gmail.com", "abcdefghijklmnop"));
        }
        catch { gmailTlsRejected = true; }
        check(gmailTlsRejected, "R60 Gmail configuration rejects disabled STARTTLS");

        return Task.CompletedTask;
    }
}
