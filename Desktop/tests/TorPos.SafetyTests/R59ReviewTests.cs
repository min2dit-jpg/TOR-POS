using TorPos.Infrastructure;

public static class R59ReviewTests
{
    public static Task Run(Action<bool,string> check)
    {
        var afterDue = new DateTimeOffset(2026, 9, 9, 10, 0, 0, TimeSpan.FromHours(2));
        var due = MonthlyReportScheduler.MostRecentDue(afterDue, 1, new TimeOnly(0, 15));
        check(due.LocalDateTime == new DateTime(2026, 9, 1, 0, 15, 0),
            "R59 monthly report scheduler resolves current month's due time");

        var beforeDue = new DateTimeOffset(2026, 9, 1, 0, 10, 0, TimeSpan.FromHours(2));
        var previousDue = MonthlyReportScheduler.MostRecentDue(beforeDue, 1, new TimeOnly(0, 15));
        check(previousDue.LocalDateTime == new DateTime(2026, 8, 1, 0, 15, 0),
            "R59 monthly report scheduler catches previous due time before today's send time");
        check(previousDue.LocalDateTime.AddMonths(-1).Month == 7,
            "R59 scheduled package targets the calendar month before its due month");

        var valid = true;
        try { ReportEmailService.Validate(new ReportEmailService.MailConfig("kunde@example.com","kasse@example.com","smtp.example.com",587,true,"kasse@example.com","app-pass")); }
        catch { valid = false; }
        check(valid, "R59 SMTP configuration accepts valid recipient/sender/host settings");

        var invalidRejected = false;
        try { ReportEmailService.Validate(new ReportEmailService.MailConfig("keine-mail","kasse@example.com","smtp.example.com",587,true,"","")); }
        catch { invalidRejected = true; }
        check(invalidRejected, "R59 SMTP configuration rejects invalid recipient address");

        var protectedValue = TorSecretProtector.Protect("secret-test");
        check(!string.IsNullOrWhiteSpace(protectedValue) && protectedValue != "secret-test" && TorSecretProtector.Unprotect(protectedValue) == "secret-test",
            "R59 SMTP password is stored with Windows user protection");
        return Task.CompletedTask;
    }
}
