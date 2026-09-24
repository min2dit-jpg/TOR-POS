using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace TorPos.Core;

/// <summary>
/// Werbe-TV: the till publishes its advertising slides as a small web page on
/// the local network, so any Smart-TV browser (Samsung, LG, Android TV) can
/// show them. Independent of the Kundendisplay - it has its own switch,
/// content choice and interval and uses the same Werbebilder folder.
///
/// The page is read-only and carries only what is already public in the shop
/// (pictures, article names, shelf prices). It is still kept off the Internet:
/// only private LAN addresses are answered, and the address contains a random
/// code so it cannot simply be guessed. Everything here is pure; the socket
/// server lives in the App.
/// </summary>
public static class AdTv
{
    public const string EnabledKey = "device.ad_tv.enabled";
    public const string SourceKey = "device.ad_tv.source";
    public const string IntervalKey = "device.ad_tv.interval_seconds";
    public const string PortKey = "device.ad_tv.port";
    public const string CodeKey = "device.ad_tv.code";

    public const int DefaultPort = 8095;
    public static readonly IReadOnlyList<int> Ports = new[] { 8095, 8096, 8097, 8098 };

    /// <summary>Letters and digits that cannot be confused on a TV remote (no 0/o, 1/l/i).</summary>
    public const string CodeAlphabet = "abcdefghjkmnpqrstuvwxyz23456789";
    public const int CodeLength = 8;

    public static AdTvSettings ReadSettings(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var enabled =
            values.TryGetValue(EnabledKey, out var rawEnabled) &&
            bool.TryParse(rawEnabled, out var parsedEnabled) &&
            parsedEnabled;

        // Same source/interval rules as the Kundendisplay advertising.
        var ads = CustomerDisplayAds.ReadSettings(new Dictionary<string, string>
        {
            [CustomerDisplayAds.EnabledKey] = "true",
            [CustomerDisplayAds.SourceKey] = values.GetValueOrDefault(SourceKey, ""),
            [CustomerDisplayAds.IntervalKey] = values.GetValueOrDefault(IntervalKey, "")
        });

        var port = DefaultPort;
        if (values.TryGetValue(PortKey, out var rawPort) &&
            int.TryParse(rawPort, out var parsedPort) &&
            Ports.Contains(parsedPort))
        {
            port = parsedPort;
        }

        var code = values.GetValueOrDefault(CodeKey, "");
        return new AdTvSettings(enabled, ads.Source, ads.Interval, port, IsValidCode(code) ? code : "");
    }

    /// <summary>The slide rules for <see cref="CustomerDisplayAds.Combine"/>.</summary>
    public static CustomerDisplayAdSettings SlideSettings(AdTvSettings settings) =>
        new(true, settings.Source, settings.Interval);

    public static string NewCode()
    {
        Span<char> code = stackalloc char[CodeLength];
        for (var i = 0; i < code.Length; i++)
            code[i] = CodeAlphabet[RandomNumberGenerator.GetInt32(CodeAlphabet.Length)];
        return new string(code);
    }

    public static bool IsValidCode(string? code) =>
        code is { Length: CodeLength } && code.All(c => CodeAlphabet.Contains(c));

    /// <summary>
    /// Only the shop's own network (and the till itself) may open the page:
    /// loopback and the private IPv4 ranges 10/8, 172.16/12, 192.168/16.
    /// </summary>
    public static bool IsAllowedClient(IPAddress? address)
    {
        if (address is null)
            return false;
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address))
            return true;
        if (address.AddressFamily != AddressFamily.InterNetwork)
            return false;

        var b = address.GetAddressBytes();
        return b[0] == 10 ||
               (b[0] == 172 && b[1] is >= 16 and <= 31) ||
               (b[0] == 192 && b[1] == 168);
    }

    public static string Url(IPAddress address, int port, string code) =>
        $"http://{address}:{port}/tv/{code}";

    /// <summary>
    /// Maps a request line to what is served. Anything that is not exactly
    /// the page, the playlist or a numbered picture of the current code is
    /// "not found" - there are no file names in the URL, so nothing outside
    /// the slide list can be requested.
    /// </summary>
    public static AdTvRoute Route(string method, string target, string code)
    {
        if (method is not ("GET" or "HEAD"))
            return AdTvRoute.MethodNotAllowed;
        if (!IsValidCode(code) || string.IsNullOrEmpty(target))
            return AdTvRoute.NotFound;

        var path = target;
        var query = path.IndexOf('?');
        if (query >= 0)
            path = path[..query];

        var parts = path.Split('/');
        // "", "tv", code [, "playlist" | "img", n]
        if (parts.Length < 3 || parts[0] != "" || parts[1] != "tv" || !SameCode(parts[2], code))
            return AdTvRoute.NotFound;

        if (parts.Length == 3 || (parts.Length == 4 && parts[3] == ""))
            return AdTvRoute.Page;
        if (parts.Length == 4 && parts[3] == "playlist")
            return AdTvRoute.Playlist;
        if (parts.Length == 5 && parts[3] == "img" &&
            parts[4].Length is > 0 and <= 3 && parts[4].All(char.IsAsciiDigit))
        {
            return AdTvRoute.Image(int.Parse(parts[4]));
        }

        return AdTvRoute.NotFound;
    }

    private static bool SameCode(string candidate, string code) =>
        candidate.Length == code.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(candidate), Encoding.ASCII.GetBytes(code));

    /// <summary>A short fingerprint of the slide list; the TV reloads when it changes.</summary>
    public static string Version(IEnumerable<string> parts)
    {
        var joined = string.Join("\u001f", parts);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(joined)))[..12].ToLowerInvariant();
    }

    /// <summary>
    /// The playlist the TV polls. Pictures are referenced by number only; no
    /// file path of the till ever leaves the PC.
    /// </summary>
    public static string PlaylistJson(
        IReadOnlyList<CustomerDisplaySlide> slides,
        TimeSpan interval,
        string companyName,
        string code,
        string version)
    {
        var basePath = $"/tv/{code}/";
        var items = slides.Select((slide, index) => new
        {
            kind = slide.Kind == CustomerDisplaySlideKind.Image ? "image" : "product",
            img = $"{basePath}img/{index}?v={version}",
            title = slide.Title,
            price = slide.PriceText,
            oldPrice = slide.OldPriceText,
            badge = slide.Badge
        });

        return JsonSerializer.Serialize(new
        {
            version,
            intervalSeconds = (int)Math.Round(interval.TotalSeconds),
            company = companyName,
            slides = items
        });
    }

    public static string ContentType(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            _ => "application/octet-stream"
        };

    /// <summary>
    /// The TV page. Plain ES5 and old-style CSS so the built-in browsers of
    /// Samsung (Tizen), LG (webOS) and Android TVs all run it. Texts from the
    /// playlist are inserted as text nodes, never as HTML.
    /// </summary>
    public static string PageHtml(string companyName, string code)
    {
        var title = WebUtility.HtmlEncode(companyName);
        var company = JsonSerializer.Serialize(companyName);
        return PageTemplate
            .Replace("__TITLE__", title, StringComparison.Ordinal)
            .Replace("__BASE__", $"/tv/{code}/", StringComparison.Ordinal)
            .Replace("__COMPANY__", company, StringComparison.Ordinal);
    }

    private const string PageTemplate = """
<!DOCTYPE html>
<html lang="de">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width,initial-scale=1">
<meta name="referrer" content="no-referrer">
<title>__TITLE__</title>
<style>
html,body{margin:0;padding:0;width:100%;height:100%;background:#000;overflow:hidden;cursor:none;font-family:Arial,Helvetica,sans-serif;color:#fff}
.slide{position:absolute;left:0;top:0;width:100%;height:100%;opacity:0;-webkit-transition:opacity .8s ease;transition:opacity .8s ease}
.slide.on{opacity:1}
.box{position:absolute;left:0;top:0;width:100%;height:100%}
.full{width:100%;height:100%;object-fit:contain}
.card{background:#111827}
.pic{position:absolute;left:4%;top:8%;width:52%;height:84%;object-fit:contain}
.txt{position:absolute;left:60%;top:0;width:36%;height:100%;display:table}
.in{display:table-cell;vertical-align:middle}
.title{font-size:5.5vw;font-weight:bold;line-height:1.1}
.old{font-size:3.2vw;color:#9CA3AF;text-decoration:line-through;margin-top:3vh}
.price{font-size:7.5vw;font-weight:bold;color:#FACC15;margin-top:2vh}
.badge{display:inline-block;background:#DC2626;font-size:3.2vw;font-weight:bold;padding:1vh 2vw;border-radius:2vw;margin-top:3vh}
.welcome{display:table;text-align:center}
.welcome div{display:table-cell;vertical-align:middle;font-size:6vw;font-weight:bold}
</style>
</head>
<body>
<div id="a" class="slide"></div><div id="b" class="slide"></div>
<script>
(function () {
  var base = "__BASE__";
  var company = __COMPANY__;
  var slides = [], interval = 8000, version = "", idx = -1, timer = null;
  var front = document.getElementById("a"), back = document.getElementById("b");

  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text) e.appendChild(document.createTextNode(text));
    return e;
  }

  function render(s) {
    var box = el("div", "box");
    if (!s) {
      box.className = "box welcome";
      box.appendChild(el("div", "", company));
      return box;
    }
    var img = new Image();
    img.src = s.img;
    if (s.kind === "image") {
      img.className = "full";
      box.appendChild(img);
      return box;
    }
    box.className = "box card";
    img.className = "pic";
    box.appendChild(img);
    var txt = el("div", "txt"), inner = el("div", "in");
    inner.appendChild(el("div", "title", s.title));
    if (s.oldPrice) inner.appendChild(el("div", "old", s.oldPrice));
    inner.appendChild(el("div", "price", s.price));
    if (s.badge) inner.appendChild(el("div", "badge", s.badge));
    txt.appendChild(inner);
    box.appendChild(txt);
    return box;
  }

  function show(s) {
    while (back.firstChild) back.removeChild(back.firstChild);
    back.appendChild(render(s));
    back.className = "slide on";
    front.className = "slide";
    var t = front; front = back; back = t;
  }

  function next() {
    if (!slides.length) { show(null); return; }
    idx = (idx + 1) % slides.length;
    show(slides[idx]);
    if (slides.length > 1) {
      var preload = new Image();
      preload.src = slides[(idx + 1) % slides.length].img;
    }
  }

  function load() {
    var x = new XMLHttpRequest();
    x.open("GET", base + "playlist?t=" + new Date().getTime(), true);
    x.onreadystatechange = function () {
      if (x.readyState !== 4 || x.status !== 200) return;
      var d;
      try { d = JSON.parse(x.responseText); } catch (e) { return; }
      if (d.version === version) return;
      version = d.version;
      slides = d.slides || [];
      interval = Math.max(4, d.intervalSeconds || 8) * 1000;
      if (d.company) company = d.company;
      idx = -1;
      next();
      if (timer) clearInterval(timer);
      timer = setInterval(next, interval);
    };
    x.send();
  }

  document.body.onclick = function () {
    var d = document.documentElement;
    var full = d.requestFullscreen || d.webkitRequestFullscreen;
    if (full) { try { full.call(d); } catch (e) { } }
  };

  show(null);
  load();
  setInterval(load, 60000);
})();
</script>
</body>
</html>
""";
}

public sealed record AdTvSettings(bool Enabled, string Source, TimeSpan Interval, int Port, string Code);

public readonly record struct AdTvRoute(AdTvRouteKind Kind, int ImageIndex = -1)
{
    public static AdTvRoute Page => new(AdTvRouteKind.Page);
    public static AdTvRoute Playlist => new(AdTvRouteKind.Playlist);
    public static AdTvRoute NotFound => new(AdTvRouteKind.NotFound);
    public static AdTvRoute MethodNotAllowed => new(AdTvRouteKind.MethodNotAllowed);
    public static AdTvRoute Image(int index) => new(AdTvRouteKind.Image, index);
}

public enum AdTvRouteKind { Page, Playlist, Image, NotFound, MethodNotAllowed }
