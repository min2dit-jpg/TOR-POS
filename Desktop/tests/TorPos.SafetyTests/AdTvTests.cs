using System.Net;
using TorPos.Core;

// Werbe-TV: the till serves its advertising as a web page for Smart-TVs in
// the shop's network. These checks pin what keeps that page harmless: off by
// default, only the shop's own network, only the page/playlist/pictures of
// the current code, and never a file path of the till in what the TV gets.
public static class AdTvTests
{
    public static Task Run(Action<bool, string> assert)
    {
        var defaults = AdTv.ReadSettings(new Dictionary<string, string>());
        var set = AdTv.ReadSettings(new Dictionary<string, string>
        {
            [AdTv.EnabledKey] = "true",
            [AdTv.SourceKey] = "bilder",
            [AdTv.IntervalKey] = "2",
            [AdTv.PortKey] = "8097",
            [AdTv.CodeKey] = "abcd2345"
        });
        var badPort = AdTv.ReadSettings(new Dictionary<string, string> { [AdTv.PortKey] = "80", [AdTv.CodeKey] = "../etc" });
        assert(
            !defaults.Enabled && defaults.Port == AdTv.DefaultPort && defaults.Code == "" &&
            defaults.Source == CustomerDisplayAds.SourceBoth &&
            set.Enabled && set.Port == 8097 && set.Code == "abcd2345" &&
            set.Source == CustomerDisplayAds.SourceImages &&
            set.Interval == TimeSpan.FromSeconds(CustomerDisplayAds.MinIntervalSeconds) &&
            badPort.Port == AdTv.DefaultPort && badPort.Code == "",
            "the Werbe-TV is off by default, uses only the offered ports and ignores a malformed address code");

        var codes = Enumerable.Range(0, 50).Select(_ => AdTv.NewCode()).ToArray();
        assert(
            codes.All(AdTv.IsValidCode) && codes.Distinct().Count() == codes.Length &&
            codes.All(c => !c.Any(ch => ch is '0' or 'o' or '1' or 'l' or 'i')),
            "TV address codes are random, unique and avoid characters that are confused on a TV remote");

        assert(
            AdTv.IsAllowedClient(IPAddress.Parse("192.168.178.40")) &&
            AdTv.IsAllowedClient(IPAddress.Parse("10.0.0.7")) &&
            AdTv.IsAllowedClient(IPAddress.Parse("172.20.1.2")) &&
            AdTv.IsAllowedClient(IPAddress.Parse("127.0.0.1")) &&
            AdTv.IsAllowedClient(IPAddress.Parse("::ffff:192.168.1.5")) &&
            !AdTv.IsAllowedClient(IPAddress.Parse("8.8.8.8")) &&
            !AdTv.IsAllowedClient(IPAddress.Parse("172.32.0.1")) &&
            !AdTv.IsAllowedClient(IPAddress.Parse("2001:db8::1")) &&
            !AdTv.IsAllowedClient(null),
            "only the till itself and private LAN addresses may open the Werbe-TV page");

        const string code = "abcd2345";
        assert(
            AdTv.Route("GET", "/tv/abcd2345", code).Kind == AdTvRouteKind.Page &&
            AdTv.Route("GET", "/tv/abcd2345/", code).Kind == AdTvRouteKind.Page &&
            AdTv.Route("HEAD", "/tv/abcd2345/playlist?t=1", code).Kind == AdTvRouteKind.Playlist &&
            AdTv.Route("GET", "/tv/abcd2345/img/3?v=x", code) == AdTvRoute.Image(3),
            "the TV gets exactly the page, the playlist and the numbered pictures");

        assert(
            AdTv.Route("GET", "/tv/abcd2346", code).Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("GET", "/", code).Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("GET", "/tv/abcd2345/img/../../torpos.db", code).Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("GET", "/tv/abcd2345/img/-1", code).Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("GET", "/tv/abcd2345/img/C:%5Ctorpos.db", code).Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("GET", "/tv/abcd2345", "").Kind == AdTvRouteKind.NotFound &&
            AdTv.Route("POST", "/tv/abcd2345/playlist", code).Kind == AdTvRouteKind.MethodNotAllowed,
            "a wrong code, a file name, a path escape or a write request never reaches anything on the till");

        var slides = new[]
        {
            CustomerDisplaySlide.ForImage(@"C:\Users\Kasse\AppData\Roaming\TOR-Einzelhandel\CustomerDisplayAds\01.png"),
            new CustomerDisplaySlide(CustomerDisplaySlideKind.Product, @"C:\Bilder\cola.jpg", "Cola <b>0,33</b>", "1,00 €", "1,20 €", "-17 %")
        };
        var playlist = AdTv.PlaylistJson(slides, TimeSpan.FromSeconds(8), "Späti", code, "v1");
        assert(
            !playlist.Contains("C:", StringComparison.OrdinalIgnoreCase) &&
            !playlist.Contains("AppData", StringComparison.OrdinalIgnoreCase) &&
            !playlist.Contains("cola.jpg", StringComparison.OrdinalIgnoreCase) &&
            playlist.Contains("/tv/abcd2345/img/1?v=v1", StringComparison.Ordinal) &&
            !playlist.Contains("<b>", StringComparison.Ordinal),
            "the playlist names pictures only by number - no file path of the till leaves the PC - and escapes article texts");

        var page = AdTv.PageHtml("</script><script>alert(1)</script>", code);
        assert(
            !page.Contains("</script><script>alert(1)", StringComparison.Ordinal) &&
            page.Contains("createTextNode", StringComparison.Ordinal) &&
            !page.Contains("innerHTML", StringComparison.Ordinal) &&
            !page.Contains("=>", StringComparison.Ordinal),
            "the TV page cannot be broken by the company name, inserts texts as text and runs on older TV browsers (ES5)");

        return Task.CompletedTask;
    }
}
