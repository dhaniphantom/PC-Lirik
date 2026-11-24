using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using MeCab;

namespace LirikMelayang;

// Lyrics in a non-Latin script become Latin letters; Latin lyrics stay as they are.
static class Romanize
{
    const string Kana = @"[぀-ヿ]", Japanese = @"[々぀-ヿ㐀-鿿]+", Hangul = @"[가-힣]+";

    static readonly object Gate = new(); // Neither the tagger nor the transliterators are thread-safe.
    static readonly Lazy<MeCabTagger> Tagger = new(() =>
        MeCabTagger.Create(new MeCabParam { DicDir = Path.Combine(AppContext.BaseDirectory, "dic") }));
    static readonly Lazy<IntPtr> KanaToLatin = new(() => Open("Hiragana-Katakana; Katakana-Latin; Latin-ASCII"));
    static readonly Lazy<IntPtr> AnyToLatin = new(() => Open("Russian-Latin/BGN; Any-Latin; Latin-ASCII"));

    [DllImport("icu.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr utrans_openU(string id, int length, int direction, IntPtr rules, int rulesLength, IntPtr error, out int status);
    [DllImport("icu.dll", CharSet = CharSet.Unicode)]
    static extern void utrans_transUChars(IntPtr handle, [In, Out] char[] text, ref int length, int capacity, int start, ref int limit, out int status);

    public static List<Line> Convert(List<Line> lines)
    {
        // Kana anywhere means the song is Japanese; otherwise its kanji would be read as Mandarin.
        var japanese = lines.Any(l => Regex.IsMatch(l.Text, Kana));
        lock (Gate)
        {
            try { return lines.Select(l => l with { Text = Text(l.Text, japanese) }).ToList(); }
            catch (Exception) { return lines; } // Original lyrics beat no lyrics.
        }
    }

    static string Text(string text, bool japanese)
    {
        if (!text.Any(IsForeign)) return text;
        if (japanese) text = Regex.Replace(text, Japanese, m => " " + FromJapanese(m.Value) + " ");
        text = Regex.Replace(text, Hangul, m => FromKorean(m.Value));
        // Kanji without a known reading stays kanji in a Japanese song.
        text = Transliterate(japanese ? KanaToLatin.Value : AnyToLatin.Value, text);
        text = Regex.Replace(text, @"\s+", " ");
        return Regex.Replace(text, @" (?=[,.!?)\]」』])|(?<=[(\[「『]) ", "").Trim();
    }

    // Letters from Greek onward, except Vietnamese letters and full-width Latin.
    static bool IsForeign(char c) =>
        char.IsLetter(c) && c >= 'Ͱ' && c is not (>= 'Ḁ' and <= 'ỿ') and not (>= 'Ａ' and <= 'ｚ');

    static string FromJapanese(string text)
    {
        var result = new StringBuilder();
        foreach (var node in Tagger.Value.ParseToNodes(text))
        {
            if (node.Stat is MeCabNodeStat.Bos or MeCabNodeStat.Eos) continue;
            var feature = node.Feature.Split(',');
            var known = feature.Length > 8;
            // Particles are spelled differently from how they sound: は is "wa", へ is "e".
            var reading = !known ? node.Surface : feature[0] == "助詞" ? feature[8] : feature[7];
            // Verb endings belong to the verb: だっ + た is "datta".
            var ending = feature[0] == "助動詞" || feature[1] is "接続助詞" or "接尾";
            if (result.Length > 0 && !ending) result.Append(' ');
            result.Append(reading);
        }
        // Converted here so that a doubled consonant never spans a space.
        return Transliterate(KanaToLatin.Value, result.ToString()).Replace("u~", "").Replace("~", "");
    }

    static readonly string[] Onsets = "g kk n d tt r m b pp s ss _ j jj ch k t p h".Replace("_", "").Split(' ');
    static readonly string[] Vowels = "a ae ya yae eo e yeo ye o wa wae oe yo u wo we wi yu eu ui i".Split(' ');
    // Per final consonant: its sound, then what stays and what moves when a vowel follows.
    static readonly string[][] Codas = ("// k//g k//kk k/k/s n//n n/n/j n//n t//d l//r k/l/g m/l/m l/l/b l/l/s l/l/t p/l/p l//r "
        + "m//m p//b p/p/s t//s t//ss ng/ng/ t//j t//ch k//k t//t p//p t//").Split(' ').Select(c => c.Split('/')).ToArray();

    // Revised Romanization with the sound changes between the syllables of a word.
    static string FromKorean(string word)
    {
        const int N = 2, R = 5, M = 6, S = 9, Silent = 11, H = 18, VowelI = 20;
        var result = new StringBuilder();
        var moved = ""; // Onset decided by the previous syllable's final consonant.
        var decided = false;
        for (var i = 0; i < word.Length; i++)
        {
            var code = word[i] - 0xAC00;
            result.Append(decided ? moved : Onsets[code / 588]).Append(Vowels[code % 588 / 28]);
            var coda = code % 28;
            var parts = Codas[coda];
            decided = false;
            if (coda == 0) continue;
            if (i + 1 == word.Length) { result.Append(parts[0]); continue; }

            var nextCode = word[i + 1] - 0xAC00;
            var onset = nextCode / 588;
            string stay = parts[0], move = Onsets[onset];
            var aspirated = coda is 6 or 15 or 27; // ㄶ ㅀ ㅎ
            if (onset == Silent)
            {
                (stay, move) = (parts[1], parts[2]);
                if (nextCode % 588 / 28 == VowelI && (coda is 7 or 25)) move = coda == 7 ? "j" : "ch"; // ㄷ ㅌ before 이
            }
            else if (aspirated && onset is 0 or 3 or 12)
                (stay, move) = (coda == 6 ? "n" : coda == 15 ? "l" : "", onset == 0 ? "k" : onset == 3 ? "t" : "ch");
            else if (coda == 27 && onset == S) (stay, move) = ("", "s");
            else if (onset == H && coda is (1 or 9)) (stay, move) = (coda == 9 ? "l" : "", "k");
            else if (onset == H && coda is (7 or 19)) (stay, move) = ("", "t");
            else if (onset == H && coda == 17) (stay, move) = ("", "p");
            else if (onset == H && coda == 22) (stay, move) = ("", "ch");
            else if (onset is N or M)
            {
                if (stay == "l" && onset == N) move = "l";
                else stay = stay switch { "k" => "ng", "t" => "n", "p" => "m", _ => stay };
            }
            else if (onset == R)
                (stay, move) = stay switch
                {
                    "l" or "n" => ("l", "l"),
                    "k" => ("ng", "n"),
                    "p" => ("m", "n"),
                    "t" => ("n", "n"),
                    _ => (stay, "n"),
                };
            result.Append(stay);
            moved = move;
            decided = true;
        }
        return result.ToString();
    }

    public static bool SelfTest()
    {
        (string In, string Out)[] cases =
        [
            ("Don't stop, café señorita đừng", "Don't stop, café señorita đừng"),
            ("사랑해요 좋아 같이", "saranghaeyo joa gachi"),
            ("한국말 설날 신라 독립", "hangungmal seollal silla dongnip"),
            ("괜찮아 않고 없어 있어 좋습니다", "gwaenchana anko eopseo isseo joseumnida"),
            ("못해 잡혀 왕 oh 너를", "motae japyeo wang oh neoreul"),
            ("我爱你，你好", "wo ai ni, ni hao"),
            ("Привет жизнь", "Privet zhizn'"),
            ("君の名は love", "kimi no na wa love"),
            ("夜に駆ける", "yoru ni kakeru"),
            ("コーヒーを飲んだ。", "kohi wo nonda."),
        ];
        return cases.All(c => Convert([new Line(default, c.In)])[0].Text == c.Out);
    }

    static IntPtr Open(string id)
    {
        var handle = utrans_openU(id, id.Length, 0, IntPtr.Zero, 0, IntPtr.Zero, out var status);
        return status > 0 ? throw new InvalidOperationException($"ICU {id}: {status}") : handle;
    }

    static string Transliterate(IntPtr handle, string text)
    {
        var buffer = new char[text.Length * 12 + 64];
        text.CopyTo(0, buffer, 0, text.Length);
        int length = text.Length, limit = text.Length;
        utrans_transUChars(handle, buffer, ref length, buffer.Length, 0, ref limit, out var status);
        return status > 0 ? text : new string(buffer, 0, length);
    }
}
