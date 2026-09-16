using TorPos.App;
using TorPos.Infrastructure;

public static class R58ReviewTests
{
    public static Task Run(Action<bool,string> check)
    {
        check(MainWindow.ProductVisualRowsForCount(4, 5, 7) == 3,
            "R58 sparse 7-item article page uses three balanced rows");
        check(MainWindow.ProductVisualRowsForCount(4, 5, 20) == 5,
            "R58 full article page keeps configured row count");
        check(MainWindow.ProductVisualRowsForCount(4, 2, 1) == 2,
            "R58 never exceeds a smaller configured row count");

        check(StarMcPrint3PrinterService.PrinterStatusProblem(0x00000080)?.Contains("OFFLINE") == true,
            "R58 Windows printer probe recognizes offline state");
        check(StarMcPrint3PrinterService.PrinterStatusProblem(0x00000010)?.Contains("Papier") == true,
            "R58 Windows printer probe recognizes paper-out state");
        check(StarMcPrint3PrinterService.PrinterStatusProblem(0) is null,
            "R58 ready/unknown-zero printer state is not falsely blocked");
        return Task.CompletedTask;
    }
}
