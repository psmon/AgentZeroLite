using System.Text.Json;
using Agent.Common.Web;

namespace ZeroCommon.Tests;

/// <summary>The watch's most-asked question, reduced to numbers a small model can read (M0032 follow-up #1).</summary>
[Trait("Category", "Web")]
public sealed class WeatherLookupTests
{
    [Theory]
    [InlineData("서울의 오늘 날씨 어떻게 돼?", "서울")]
    [InlineData("오늘의 날씨 어떻게 돼?", "")]
    [InlineData("부산은 지금 날씨 어때", "부산")]
    [InlineData("대구 기온 알려줘", "대구")]
    [InlineData("weather in Tokyo today?", "Tokyo")]
    [InlineData("what's the weather now", "")]
    public void Place_is_extracted_from_the_question(string query, string expected)
    {
        Assert.True(WeatherLookup.IsWeatherQuery(query));
        Assert.Equal(expected, WeatherLookup.ExtractPlace(query));
    }

    [Theory]
    [InlineData("이문세 노래 틀어줘")]
    [InlineData("AgentZeroLite github")]
    public void Non_weather_queries_are_left_alone(string query)
        => Assert.False(WeatherLookup.IsWeatherQuery(query));

    [Fact]
    public void J1_document_is_reduced_to_the_answerable_numbers()
    {
        const string j1 = """
            {"current_condition":[{"temp_C":"21","FeelsLikeC":"22","humidity":"63","windspeedKmph":"14",
              "weatherDesc":[{"value":"Partly cloudy"}],"lang_ko":[{"value":"구름 조금"}]}],
             "nearest_area":[{"areaName":[{"value":"Seoul"}]}],
             "weather":[{"maxtempC":"27","mintempC":"18","hourly":[{"chanceofrain":"0"},{"chanceofrain":"40"},{"chanceofrain":"10"}]}]}
            """;
        var o = WeatherLookup.Summarize(j1, "서울")!;
        Assert.Equal("서울", o["location"]!.GetValue<string>());
        Assert.Equal(21, o["temp_c"]!.GetValue<int>());
        Assert.Equal(22, o["feels_like_c"]!.GetValue<int>());
        Assert.Equal("구름 조금", o["condition"]!.GetValue<string>());
        Assert.Equal(27, o["today_max_c"]!.GetValue<int>());
        Assert.Equal(18, o["today_min_c"]!.GetValue<int>());
        Assert.Equal(40, o["rain_chance_pct"]!.GetValue<int>());

        var located = WeatherLookup.Summarize(j1, "")!;
        Assert.Equal("Seoul", located["location"]!.GetValue<string>());
    }

    [Fact]
    public void Garbage_is_null_not_an_exception()
    {
        Assert.Null(WeatherLookup.Summarize("<html>blocked</html>", "x"));
        Assert.Null(WeatherLookup.Summarize("{}", "x"));
        Assert.Null(WeatherLookup.Summarize("", "x"));
    }

    [Fact]
    public void Url_is_escaped()
        => Assert.Equal("https://wttr.in/%EC%84%9C%EC%9A%B8?format=j1&lang=ko", WeatherLookup.BuildUrl("서울"));
}
