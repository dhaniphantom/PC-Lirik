using System.Globalization;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace LirikMelayang;

record Line(TimeSpan Time, string Text);
record Track(double? Duration, string? SyncedLyrics, string? AlbumName = null);

static class Lyrics
{
    // Lines appear this early, so that a line has mostly rolled into place by the time it is sung.
    public static readonly TimeSpan RollLead = TimeSpan.FromMilliseconds(250);

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
        if (i + 1 < lines.Count)
        {
            // Fully sung when the next line rolls in, unless that leaves too little time.
            var gap = lines[i + 1].Time - lines[i].Time;
            var limit = gap > RollLead * 2 ? gap - RollLead : gap;
            if (limit < length) length = limit;
        }
        var elapsed = position - lines[i].Time;
        if (elapsed >= length) return (text.Length, TimeSpan.Zero);
        return ((int)Math.Round(text.Length * (elapsed / length)), length - elapsed);
    }

    // Submissions for one song can differ by many seconds. The timing most of them agree on wins, and the album that
    // is playing counts as two extra votes. More than 2 s off in duration is usually another recording.
    public static Track? Pick(IEnumerable<Track> tracks, TimeSpan duration, string album)
    {
        var all = tracks.Where(t => !string.IsNullOrEmpty(t.SyncedLyrics) && t.Duration is double d
                                    && Math.Abs(d - duration.TotalSeconds) <= 2)
            .Select(t => (Track: t, Starts: Parse(t.SyncedLyrics!).Where(l => l.Text != "").Select(l => l.Time).ToList()))
            .Where(t => t.Starts.Count > 0).ToList();
        return all.OrderByDescending(a => all.Sum(b => Agreement(a.Starts, b.Starts))
                + (album != "" && album.Equals(a.Track.AlbumName, StringComparison.OrdinalIgnoreCase) ? 2 : 0))
            .ThenBy(a => Math.Abs(a.Track.Duration!.Value - duration.TotalSeconds))
            .FirstOrDefault().Track;
    }

    // Share of lines in a that start within 0.3 s of a line in b.
    static double Agreement(List<TimeSpan> a, List<TimeSpan> b) =>
        a.Count(x => b.Any(y => (x - y).Duration() <= TimeSpan.FromSeconds(0.3))) / (double)a.Count;

    // Empty list: no synced lyrics match. Throws on network failure.
    public static async Task<List<Line>> FetchAsync(string title, string artist, string album, TimeSpan duration)
    {
        // Spotify joins artists with ", " but LRCLIB often lists only the first one.
        foreach (var name in new[] { artist, artist.Split(", ")[0] }.Distinct())
        {
            var url = $"https://lrclib.net/api/search?track_name={Uri.EscapeDataString(title)}&artist_name={Uri.EscapeDataString(name)}";
            var tracks = await Http.GetFromJsonAsync<List<Track>>(url) ?? [];
            if (Pick(tracks, duration, album)?.SyncedLyrics is string lrc) return await Task.Run(() => Romanize.Convert(Parse(lrc)));
        }
        return [];
    }

    public static bool SelfTest()
    {
        var lines = Parse("[ar:X]\n[00:10.50] b\r\n[00:05.00][01:00.00]a\n[00:20]\nno stamp");
        // At 5 characters per second, line 0 is limited by the next line (2 s minus the roll) and line 1 by its length (2 s).
        List<Line> pace = [new(TimeSpan.FromSeconds(10), new string('a', 14)), new(TimeSpan.FromSeconds(12), new string('b', 10))];
        Track[] tracks = [new(200, null), new(203, "[00:01]far"), new(200, "[00:01]odd"), new(201, "[00:09]b"),
            new(202, "[00:09.2]c"), new(null, "[00:01]x"), new(200, "no stamp")];
        Track[] versions = [new(200, "[00:01]a", "A"), new(200, "[00:09]b", "B")];
        return lines.Select(l => (l.Time.TotalSeconds, l.Text))
                   .SequenceEqual([(5, "a"), (10.5, "b"), (20, ""), (60, "a")])
            && IndexAt(lines, TimeSpan.FromSeconds(4)) == -1
            && IndexAt(lines, TimeSpan.FromSeconds(10.5)) == 1
            && IndexAt(lines, TimeSpan.FromSeconds(999)) == 3
            && Progress(pace, 0, TimeSpan.FromSeconds(10), 5) == (0, TimeSpan.FromSeconds(1.75))
            && Progress(pace, 0, TimeSpan.FromSeconds(11), 5) == (8, TimeSpan.FromSeconds(0.75))
            && Progress(pace, 1, TimeSpan.FromSeconds(13), 5) == (5, TimeSpan.FromSeconds(1))
            && Progress(pace, 1, TimeSpan.FromSeconds(60), 5) == (10, TimeSpan.Zero)
            && Pace(pace) == 7
            && Pace([]) == 8
            && Pick(tracks, TimeSpan.FromSeconds(200), "")?.SyncedLyrics == "[00:09]b"
            && Pick(versions, TimeSpan.FromSeconds(200), "b")?.AlbumName == "B"
            && Pick(tracks, TimeSpan.FromSeconds(300), "") is null;
    }
}
