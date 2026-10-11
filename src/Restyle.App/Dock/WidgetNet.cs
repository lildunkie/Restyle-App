using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Xml.Linq;

namespace Restyle.App.Docking;

/// <summary>
/// The only parts of Restyle that use the internet, and only after the user turns them on:
/// weather from Open-Meteo (free, no account or key) and headlines from a news feed the user picks.
/// Nothing about the user or their PC is sent, just the chosen city's coordinates or the feed address.
/// </summary>
internal static class WidgetNet
{
    private static readonly Lazy<HttpClient> Http = new(() =>
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Restyle/2.0");
        return client;
    });

    public sealed record Place(string Name, double Latitude, double Longitude);

    public sealed record Weather(double Temperature, double High, double Low, int Code, bool IsDay);

    public sealed record Headline(string Title, string? Link);

    /// <summary>Finds a city by name ("Paris", "Austin, Texas"). Null if nothing matches.</summary>
    public static async Task<Place?> FindPlace(string query)
    {
        var name = query.Split(',')[0].Trim();
        if (name.Length == 0)
            return null;
        var url = $"https://geocoding-api.open-meteo.com/v1/search?count=10&language=en&format=json&name={Uri.EscapeDataString(name)}";
        using var doc = JsonDocument.Parse(await Http.Value.GetStringAsync(url));
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        // "Austin, Texas" / "Paris, France": prefer a result whose state or country matches the rest.
        var hint = query.Contains(',') ? query[(query.IndexOf(',') + 1)..].Trim() : "";
        var best = results[0];
        if (hint.Length > 0)
        {
            foreach (var r in results.EnumerateArray())
            {
                if (Str(r, "admin1").Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || Str(r, "country").Contains(hint, StringComparison.OrdinalIgnoreCase)
                    || Str(r, "country_code").Equals(hint, StringComparison.OrdinalIgnoreCase))
                {
                    best = r;
                    break;
                }
            }
        }

        var region = Str(best, "admin1");
        var display = region.Length > 0 && region != Str(best, "name") ? $"{Str(best, "name")}, {region}" : Str(best, "name");
        return new Place(display, best.GetProperty("latitude").GetDouble(), best.GetProperty("longitude").GetDouble());
    }

    public static async Task<Weather> GetWeather(double latitude, double longitude, bool fahrenheit)
    {
        var url = "https://api.open-meteo.com/v1/forecast?" +
            FormattableString.Invariant($"latitude={latitude:0.###}&longitude={longitude:0.###}") +
            "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min" +
            $"&timezone=auto&forecast_days=1&temperature_unit={(fahrenheit ? "fahrenheit" : "celsius")}";
        using var doc = JsonDocument.Parse(await Http.Value.GetStringAsync(url));
        var current = doc.RootElement.GetProperty("current");
        var daily = doc.RootElement.GetProperty("daily");
        return new Weather(
            current.GetProperty("temperature_2m").GetDouble(),
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.TryGetProperty("is_day", out var day) && day.GetInt32() == 1);
    }

    /// <summary>WMO weather code → (description, weather symbol drawn with the Segoe UI Emoji font).</summary>
    public static (string Text, string Glyph) Describe(int code, bool isDay) => code switch
    {
        0 => (isDay ? "Clear" : "Clear night", isDay ? "☀" : "\U0001F319"),
        1 or 2 => ("Partly cloudy", isDay ? "⛅" : "☁"),
        3 => ("Cloudy", "☁"),
        45 or 48 => ("Fog", "\U0001F32B"),
        >= 51 and <= 57 => ("Drizzle", "\U0001F326"),
        >= 61 and <= 67 or >= 80 and <= 82 => ("Rain", "\U0001F327"),
        >= 71 and <= 77 or 85 or 86 => ("Snow", "❄"),
        >= 95 => ("Thunderstorms", "⛈"),
        _ => ("—", "☁"),
    };

    /// <summary>Reads an RSS 2.0 or Atom feed and returns its newest headlines.</summary>
    public static async Task<List<Headline>> GetHeadlines(string feedUrl)
    {
        if (!Uri.TryCreate(feedUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            return [];
        await using var stream = await Http.Value.GetStreamAsync(uri);
        using var reader = System.Xml.XmlReader.Create(stream, new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Ignore, // never fetch or expand external entities
            XmlResolver = null,
            Async = true,
        });
        var doc = await XDocument.LoadAsync(reader, LoadOptions.None, CancellationToken.None);
        XNamespace atom = "http://www.w3.org/2005/Atom";

        var items = doc.Descendants("item").Select(i => new Headline(Clean(i.Element("title")?.Value), i.Element("link")?.Value?.Trim()));
        var entries = doc.Descendants(atom + "entry").Select(e => new Headline(
            Clean(e.Element(atom + "title")?.Value),
            (e.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate") ?? e.Element(atom + "link"))?.Attribute("href")?.Value));
        return items.Concat(entries).Where(h => h.Title.Length > 0).Take(15).ToList();
    }

    /// <summary>Only web links are opened from headlines.</summary>
    public static bool IsWebLink(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out var u) && u.Scheme is "https" or "http";

    private static string Clean(string? text) =>
        System.Net.WebUtility.HtmlDecode(text ?? "").Replace('\n', ' ').Trim();

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}
