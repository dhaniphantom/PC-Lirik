using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace LirikMelayang;

record Line(TimeSpan Time, string Text);
record Track(double? Duration, string? SyncedLyrics);

static class Lyrics
{
    static readonly HttpClient Http = CreateClient();

    static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("LirikMelayang/1.0");
        return http;
    }

    public static List<Line> Parse(string lrc)
    {
        var lines = new List<Line>();
        foreach (var raw in lrc.Split('\n').Select(l => l.Trim()))
        {
            var stamps = Regex.Match(raw, @"^(?:\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]\s*)+");
            if (!stamps.Success) continue;
            for (var i = 0; i < stamps.Groups[1].Captures.Count; i++)
            {
                var minutes = int.Parse(stamps.Groups[1].Captures[i].Value, CultureInfo.InvariantCulture);
                var seconds = double.Parse(stamps.Groups[2].Captures[i].Value, CultureInfo.InvariantCulture);
                lines.Add(new Line(TimeSpan.FromSeconds(minutes * 60 + seconds), raw[stamps.Length..]));
            }
        }
        return lines.OrderBy(l => l.Time).ToList();
    }

    public static int IndexAt(List<Line> lines, TimeSpan position) =>
        lines.FindLastIndex(l => l.Time <= position);

    // Characters per second while the singer is singing. Most lines end in a pause, which lowers their rate,
    // so a fast line (75th percentile) is closer to the real pace than the average.
    public static double Pace(List<Line> lines)
    {
        var rates = lines.Zip(lines.Skip(1))
            .Where(p => p.First.Text.Length >= 8 && p.Second.Time > p.First.Time)
            .Select(p => p.First.Text.Length / (p.Second.Time - p.First.Time).TotalSeconds)
            .Order().ToList();
        return rates.Count > 0 ? rates[(int)(rates.Count * 0.75)] : 8;
    }

    // Characters of line i sung at a position, and the time left until the line is fully sung.
    // shortcut: LRCLIB times lines, not words, so this assumes an even pace; needs word timing to be exact.
    public static (int Sung, TimeSpan Left) Progress(List<Line> lines, int i, TimeSpan position, double pace)
    {
        var text = lines[i].Text;
        var length = TimeSpan.FromSeconds(Math.Max(0.5, text.Length / pace));
        if (i + 1 < lines.Count && lines[i + 1].Time - lines[i].Time < length) length = lines[i + 1].Time - lines[i].Time;
        var elapsed = position - lines[i].Time;
        if (elapsed >= length) return (text.Length, TimeSpan.Zero);
        return ((int)Math.Round(text.Length * (elapsed / length)), length - elapsed);
    }

    public static Track? Pick(IEnumerable<Track> tracks, TimeSpan duration) =>
        tracks.Where(t => !string.IsNullOrEmpty(t.SyncedLyrics) && t.Duration is double d
                          && Math.Abs(d - duration.TotalSeconds) <= 5)
              .MinBy(t => Math.Abs(t.Duration!.Value - duration.TotalSeconds));

    // Empty list: no synced lyrics match. Throws on network failure.
    public static async Task<List<Line>> FetchAsync(string title, string artist, TimeSpan duration)
    {
        // Spotify joins artists with ", " but LRCLIB often lists only the first one.
        foreach (var name in new[] { artist, artist.Split(", ")[0] }.Distinct())
        {
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(name)}";
            var tracks = await Http.GetFromJsonAsync<List<Track>>(url) ?? [];
            if (Pick(tracks, duration)?.SyncedLyrics is string lrc) return await Task.Run(() => Romanize.Convert(Parse(lrc)));
        }
        return [];
    }

    public static bool SelfTest()
    {
        var lines = Parse("[ar:X]\n[00:10.50] b\r\n[00:05.00][01:00.00]a\n[00:20]\nno stamp");
        // At 5 characters per second, line 0 is limited by the next line (2 s) and line 1 by its length (2 s).
        List<Line> pace = [new(TimeSpan.FromSeconds(10), new string('a', 20)), new(TimeSpan.FromSeconds(12), new string('b', 10))];
        Track[] tracks = [new(200, null), new(203, "far"), new(201, "near"), new(null, "x")];
        return lines.Select(l => (l.Time.TotalSeconds, l.Text))
                   .SequenceEqual([(5, "a"), (10.5, "b"), (20, ""), (60, "a")])
            && IndexAt(lines, TimeSpan.FromSeconds(4)) == -1
            && IndexAt(lines, TimeSpan.FromSeconds(10.5)) == 1
            && IndexAt(lines, TimeSpan.FromSeconds(999)) == 3
            && Progress(pace, 0, TimeSpan.FromSeconds(10), 5) == (0, TimeSpan.FromSeconds(2))
            && Progress(pace, 0, TimeSpan.FromSeconds(11), 5) == (10, TimeSpan.FromSeconds(1))
            && Progress(pace, 1, TimeSpan.FromSeconds(13), 5) == (5, TimeSpan.FromSeconds(1))
            && Progress(pace, 1, TimeSpan.FromSeconds(60), 5) == (10, TimeSpan.Zero)
            && Pace(pace) == 10
            && Pace([]) == 8
            && Pick(tracks, TimeSpan.FromSeconds(200))?.SyncedLyrics == "near"
            && Pick(tracks, TimeSpan.FromSeconds(300)) is null;
    }
}
