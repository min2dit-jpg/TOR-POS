using System.Globalization;

namespace TorPos.App;

public static class Formatting
{
    private static readonly CultureInfo De=CultureInfo.GetCultureInfo("de-DE");

    public static string Money(long cents)=>(cents/100m).ToString("C2",De);

    public static bool TryParseMoney(string? value,out long cents)
    {
        cents=0;
        if(string.IsNullOrWhiteSpace(value)) return false;
        if(!decimal.TryParse(value,NumberStyles.Number,De,out var amount) &&
           !decimal.TryParse(value.Replace(',','.'),NumberStyles.Number,CultureInfo.InvariantCulture,out amount))
            return false;
        cents=(long)Math.Round(amount*100m,MidpointRounding.AwayFromZero);
        return true;
    }
}
