using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Agent.Common.Web;

/// <summary>
/// "What's the weather?" is the first thing anyone asks a watch, and it is exactly the
/// question a search engine answers worst for a small model: the results are JavaScript
/// dashboards whose numbers never reach a headless fetch, so the model ends up telling
/// the user to "check the website" (M0032 follow-up #1, observed on the device). This
/// side-steps that: when a search query is about the weather, the search also asks
/// <c>wttr.in</c> for the current conditions as JSON — no key, one request — and hands the
/// numbers to the model as a <c>weather</c> block it can answer from directly.
/// </summary>
public static class WeatherLookup
{
    private static readonly Regex WeatherWord = new(@"날씨|기온|weather|temperature|forecast|비\s*(?:와|오|올)|눈\s*(?:와|오|올)|덥|춥",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // The place is the word right before 날씨/weather, minus particles ("서울의", "부산은").
    private static readonly Regex KoPlace = new(@"([가-힣A-Za-z]+?)(?:의|은|는|시|도|에서|에)?\s*(?:오늘|내일|지금|현재|이번\s*주)?\s*(?:날씨|기온)",
        RegexOptions.Compiled);
    private static readonly Regex EnPlace = new(@"weather\s+(?:in|for|at)\s+([A-Za-z][A-Za-z .'-]+?)(?:\s+(?:today|now|tomorrow))?\s*[?.!]?$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> NotPlaces = new(StringComparer.OrdinalIgnoreCase)
    {
        "오늘", "내일", "지금", "현재", "오늘의", "이번", "주말", "the", "today", "now", "tomorrow", "current",
    };

    public static bool IsWeatherQuery(string? query)
        => !string.IsNullOrWhiteSpace(query) && WeatherWord.IsMatch(query);

    /// <summary>The place named in the query, or "" (wttr.in then uses the caller's location).</summary>
    public static string ExtractPlace(string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var en = EnPlace.Match(query);
        if (en.Success) return Clean(en.Groups[1].Value);
        var ko = KoPlace.Match(query);
        if (ko.Success) return Clean(ko.Groups[1].Value);
        return "";

        static string Clean(string s)
        {
            s = s.Trim();
            return NotPlaces.Contains(s) ? "" : s;
        }
    }

    public static string BuildUrl(string place)
        => "https://wttr.in/" + Uri.EscapeDataString(place.Trim()) + "?format=j1&lang=ko";

    /// <summary>
    /// Reduce wttr.in's <c>format=j1</c> document to the handful of numbers a two-sentence
    /// answer needs. Returns null when the shape is not what we expect.
    /// </summary>
    public static JsonObject? Summarize(string? j1Json, string requestedPlace)
    {
        if (string.IsNullOrWhiteSpace(j1Json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(j1Json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("current_condition", out var currents) || currents.GetArrayLength() == 0) return null;
            var now = currents[0];

            var o = new JsonObject();
            var area = root.TryGetProperty("nearest_area", out var areas) && areas.GetArrayLength() > 0
                ? Str(areas[0], "areaName") : null;
            o["location"] = string.IsNullOrEmpty(requestedPlace) ? area ?? "current location" : requestedPlace;
            o["temp_c"] = Num(now, "temp_C");
            o["feels_like_c"] = Num(now, "FeelsLikeC");
            o["condition"] = Str(now, "lang_ko") ?? Str(now, "weatherDesc") ?? "";
            o["humidity_pct"] = Num(now, "humidity");
            o["wind_kmph"] = Num(now, "windspeedKmph");

            if (root.TryGetProperty("weather", out var days) && days.GetArrayLength() > 0)
            {
                var today = days[0];
                o["today_max_c"] = Num(today, "maxtempC");
                o["today_min_c"] = Num(today, "mintempC");
                if (today.TryGetProperty("hourly", out var hourly))
                {
                    var rain = 0;
                    foreach (var h in hourly.EnumerateArray())
                        if (h.TryGetProperty("chanceofrain", out var c) && int.TryParse(c.GetString(), out var v)) rain = Math.Max(rain, v);
                    o["rain_chance_pct"] = rain;
                }
            }
            o["source"] = "wttr.in";
            return o;
        }
        catch
        {
            return null;
        }
    }

    private static int? Num(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && int.TryParse(v.GetString(), out var n) ? n : null;

    /// <summary>wttr.in nests localized strings as <c>[{"value":"…"}]</c>.</summary>
    private static string? Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.String) return v.GetString();
        if (v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0 &&
            v[0].TryGetProperty("value", out var inner) && inner.ValueKind == JsonValueKind.String)
            return inner.GetString();
        return null;
    }
}
