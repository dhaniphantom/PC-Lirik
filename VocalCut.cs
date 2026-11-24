using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;

namespace LirikMelayang;

// Plays the music player's sound again without what sits in the middle of the stereo image, which is usually the vocals.
// Windows captures a player after its volume is applied, so the original is turned almost off and the copy amplified back.
class VocalCut
{
    const float Duck = 0.001f;
    const int PlayerLatency = 50;
    static readonly TimeSpan Cushion = TimeSpan.FromMilliseconds(30), MaxBuffered = TimeSpan.FromMilliseconds(200);
    static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    // Holds the player's own volume while it is turned down: Windows remembers a volume even after a crash.
    readonly string stateFile;
    AudioSessionControl? session;
    WasapiRecorder? recorder;
    WasapiPlayer? player;
    BufferedWaveProvider? buffer;
    string deviceId = "";
    float ducked;

    public VocalCut(string dataFolder) => stateFile = Path.Combine(dataFolder, "volume.txt");

    public bool Active { get; private set; }

    // How much later than the original the copy is heard.
    public TimeSpan Delay => Active ? buffer!.BufferedDuration + TimeSpan.FromMilliseconds(PlayerLatency) : TimeSpan.Zero;

    // False: the player makes no sound right now.
    public async Task<bool> StartAsync(string playerId)
    {
        Stop();
        var device = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        session = Sessions(device).FirstOrDefault(s => s.Session.State == AudioSessionState.AudioSessionStateActive
            && playerId.Contains(s.Name, StringComparison.OrdinalIgnoreCase)) is { Session: not null } found ? found.Session : null;
        if (session is null) return false;
        try
        {
            deviceId = device.ID;
            buffer = new BufferedWaveProvider(Format) { DiscardOnBufferOverflow = true };
            player = new WasapiPlayerBuilder().WithDevice(device).WithLatency(PlayerLatency).Build();
            player.Init(buffer);
            recorder = await new WasapiRecorderBuilder().WithFormat(Format)
                .WithProcessLoopback(session.GetProcessID, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync();

            var volume = session.SimpleAudioVolume.Volume;
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
            File.WriteAllLines(stateFile, [deviceId, NameOf(session)!, volume.ToString(CultureInfo.InvariantCulture)]);
            session.SimpleAudioVolume.Volume = volume * Duck;
            ducked = session.SimpleAudioVolume.Volume;
            await Task.Delay(100); // Windows fades to a new volume; the fade must not be amplified.

            var (filter, output) = (new CenterFilter(), buffer);
            recorder.DataAvailable += (data, _, _, _) => Process(filter, output, MemoryMarshal.Cast<byte, float>(data));
            recorder.StartRecording();
            player.Play();
            return Active = true;
        }
        catch
        {
            Stop();
            throw;
        }
    }

    static void Process(CenterFilter filter, BufferedWaveProvider output, ReadOnlySpan<float> input)
    {
        if (output.BufferedDuration > MaxBuffered) output.ClearBuffer(); // The two clocks drifted apart.
        if (output.BufferedDuration == TimeSpan.Zero)
        {
            var silence = new byte[(int)(Format.AverageBytesPerSecond * Cushion.TotalSeconds)];
            output.AddSamples(silence, 0, silence.Length);
        }
        var samples = Render(filter, input);
        output.AddSamples(MemoryMarshal.AsBytes(samples.AsSpan()).ToArray(), 0, samples.Length * sizeof(float));
    }

    static float[] Render(CenterFilter filter, ReadOnlySpan<float> input)
    {
        var samples = new float[input.Length];
        var loud = false;
        for (var i = 0; i + 1 < input.Length; i += 2)
        {
            float left = input[i] / Duck, right = input[i + 1] / Duck;
            loud |= Math.Abs(left) > 2 || Math.Abs(right) > 2;
            samples[i] = samples[i + 1] = Math.Clamp(filter.Next(left, right), -1, 1);
        }
        // Someone raised the player's volume, so this would be deafening.
        if (loud) Array.Clear(samples);
        return samples;
    }

    // Call regularly. Stops when the player, its volume or the output device changed, and undoes a leftover low volume.
    public void Check()
    {
        if (!Active) { Restore(); return; }
        bool same;
        try
        {
            same = Math.Abs(session!.SimpleAudioVolume.Volume - ducked) <= ducked / 2
                && session.State != AudioSessionState.AudioSessionStateExpired
                && recorder!.CaptureState == CaptureState.Capturing && player!.PlaybackState == PlaybackState.Playing
                && new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia).ID == deviceId;
        }
        catch (Exception) { same = false; } // The player or the device is gone.
        if (!same) Stop();
    }

    public void Stop()
    {
        Active = false;
        // The copy goes silent before the original comes back, so the original is never amplified.
        foreach (var part in new IDisposable?[] { player, recorder })
            try { part?.Dispose(); } catch (Exception) { } // Its device can be gone already.
        (player, recorder, session) = (null, null, null);
        Restore();
    }

    // Keeps the file until the player's session exists again, because only a live session can be changed.
    void Restore()
    {
        if (!File.Exists(stateFile)) return;
        try
        {
            var state = File.ReadAllLines(stateFile);
            if (state.Length != 3 || !float.TryParse(state[2], CultureInfo.InvariantCulture, out var volume)) { File.Delete(stateFile); return; }
            var mine = Sessions(new MMDeviceEnumerator().GetDevice(state[0])).Where(s => s.Name == state[1]).ToList();
            foreach (var (_, s) in mine)
                if (s.SimpleAudioVolume.Volume <= Duck * 1.5f) s.SimpleAudioVolume.Volume = Math.Clamp(volume, 0, 1);
            if (mine.Count > 0) File.Delete(stateFile);
        }
        catch (Exception) { } // Tried again on the next check.
    }

    static IEnumerable<(string Name, AudioSessionControl Session)> Sessions(MMDevice device)
    {
        var all = device.AudioSessionManager.Sessions;
        for (var i = 0; i < all.Count; i++)
            if (NameOf(all[i]) is string name) yield return (name, all[i]);
    }

    static string? NameOf(AudioSessionControl session)
    {
        if (session.IsSystemSoundsSession) return null;
        try { using var process = System.Diagnostics.Process.GetProcessById((int)session.GetProcessID); return process.ProcessName; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; } // Already closed.
    }

    // Left minus right removes the middle; the bass is put back because it is mixed to the middle too.
    // shortcut: also removes the snare and anything else in the middle; needs an AI model to tell voices apart.
    class CenterFilter
    {
        static readonly float Step = 1 - MathF.Exp(-2 * MathF.PI * 150 / 48000);
        float a, b, c;

        public float Next(float left, float right)
        {
            a += Step * ((left + right) / 2 - a);
            b += Step * (a - b);
            c += Step * (b - c);
            return c + (left - right) * 0.7f;
        }
    }

    public static bool SelfTest()
    {
        // Peak of the second half of one second of a tone, as captured.
        static float Peak(float frequency, float leftGain, float rightGain)
        {
            var input = new float[96000];
            for (var i = 0; i < 48000; i++)
            {
                var tone = MathF.Sin(2 * MathF.PI * frequency * i / 48000) * Duck;
                (input[i * 2], input[i * 2 + 1]) = (tone * leftGain, tone * rightGain);
            }
            return Render(new CenterFilter(), input).Skip(48000).Max(Math.Abs);
        }
        return Peak(1000, 0.5f, 0.5f) < 0.01f // Vocals in the middle.
            && Peak(1000, 0.5f, 0) is > 0.3f and < 0.4f // An instrument on the left.
            && Peak(50, 0.5f, 0.5f) is > 0.4f and < 0.5f // Bass in the middle.
            && Peak(1000, 1000, 0) == 0; // Volume raised by hand.
    }
}
