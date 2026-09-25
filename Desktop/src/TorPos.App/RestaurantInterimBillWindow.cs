using Avalonia.Controls;
using Avalonia.Layout;
using TorPos.Core;

namespace TorPos.App;

public sealed class RestaurantInterimBillWindow : Window
{
    public RestaurantInterimBillWindow(ReportPrintJob report,IReceiptPrinterService printer,ISettingsRepository settings)
    {
        var text=new TextBox { Text=string.Join(Environment.NewLine,report.Lines),IsReadOnly=true,AcceptsReturn=true,MinHeight=350 };
        var status=RestaurantEditorLayout.Label("");
        var print=RestaurantEditorLayout.Button("ZWISCHENRECHNUNG DRUCKEN");print.Name="InterimPrint";
        print.Click+=async (_,_)=>
        {
            print.IsEnabled=false;
            try
            {
                var s=await settings.LoadAllAsync();var name=s.GetValueOrDefault("device.receipt_printer.name","").Trim();
                if(name.Length==0 || s.GetValueOrDefault("device.receipt_printer.enabled","true")=="false")
                    throw new InvalidOperationException("Bitte Bondrucker unter Stammdaten → Drucker/Küche einrichten.");
                await printer.PrintReportAsync(report,name);
                status.Text="Zwischenrechnung an Drucker gesendet. Papierausdruck prüfen.";
            }
            catch(Exception ex){status.Text=ex.Message;}
            finally{print.IsEnabled=true;}
        };
        RestaurantEditorLayout.Apply(this,report.Title,new StackPanel { Children={text,status}},print);
    }
}
