using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Forms = System.Windows.Forms;

namespace LirikMelayang;

class Overlay : Window
{
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x8000000;
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hwnd, int index, int value);
    [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hwnd, int x, int y, int width, int height, bool repaint);
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    static readonly TimeSpan NoticeTime = TimeSpan.FromSeconds(4), RetryDelay = TimeSpan.FromSeconds(30),
        RollTime = TimeSpan.FromMilliseconds(700), ColorInterval = TimeSpan.FromSeconds(2);
    const double RollUp = 80, RollIn = 40, NextOpacity = 0.7;

    readonly TextBlock current = Label(30, FontWeights.SemiBold);
    readonly TextBlock leaving = Label(30, FontWeights.SemiBold);
    readonly TextBlock next = Label(20, FontWeights.Normal);
    readonly TextEffect sung = new() { Foreground = Brushes.Gold };
    readonly Border box;
    readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    GlobalSystemMediaTransportControlsSessionManager? media;
    bool unlocked, busy;
    (string Title, string Artist, int Seconds)? song;
    List<Line>? lines;
    double pace;
    // Makes up for the polling delay and for line times that people tapped in slightly late.
    public TimeSpan Lead = TimeSpan.FromMilliseconds(250);
    (string Now, string After)? shown;
    string notice = "";
    DateTime noticeUntil, nextColorCheck;
    DateTime? retryAt;

    public Overlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Title = "Lirik Melayang";
        FontFamily = new FontFamily("Segoe UI");

        Width = 1000;
        Height = 240;

        current.TextEffects = new TextEffectCollection { sung };
        // Bottom alignment lets a tall leaving line grow upward without pushing the others.
        leaving.VerticalAlignment = current.VerticalAlignment = VerticalAlignment.Bottom;
        var top = new Grid();
        top.Children.Add(leaving);
        top.Children.Add(current);
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 40) };
        stack.Children.Add(top);
        stack.Children.Add(next);
        Content = box = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 8, 16, 8), Child = stack };

        MouseLeftButtonDown += (_, e) => { if (unlocked && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        SourceInitialized += (_, _) => { SetUnlocked(false); MoveToSecondScreen(); };
        timer.Tick += async (_, _) => await TickAsync();
        timer.Start();
    }

    static TextBlock Label(double size, FontWeight weight) => new()
    {
        FontSize = size, FontWeight = weight, Opacity = 0,
        Foreground = Brushes.White,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        RenderTransformOrigin = new Point(0.5, 0),
        RenderTransform = new TransformGroup { Children = { new ScaleTransform(), new TranslateTransform() } },
        // Keeps white text legible over white pages behind the overlay.
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 6, ShadowDepth = 0, Opacity = 1 },
    };

    // Physical pixels: the screens can have different DPI. Falls back to the primary screen.
    void MoveToSecondScreen()
    {
        var area = (Forms.Screen.AllScreens.FirstOrDefault(s => !s.Primary) ?? Forms.Screen.PrimaryScreen!).WorkingArea;
        var hwnd = new WindowInteropHelper(this).Handle;
        MoveWindow(hwnd, area.Left, area.Top, 100, 100, false); // Lands on the target screen first, so GetDpiForWindow reports its DPI.
        var scale = GetDpiForWindow(hwnd) / 96.0;
        var width = Math.Min((int)(1000 * scale), area.Width);
        var height = Math.Min((int)(240 * scale), area.Height);
        MoveWindow(hwnd, area.Left + (area.Width - width) / 2, area.Bottom - height, width, height, false);
    }

    public void SetUnlocked(bool value)
    {
        unlocked = value;
        var hwnd = new WindowInteropHelper(this).Handle;
        var style = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        SetWindowLong(hwnd, GWL_EXSTYLE, value ? style & ~WS_EX_TRANSPARENT : style | WS_EX_TRANSPARENT);
        box.Background = value ? new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)) : null;
    }

    async Task TickAsync()
    {
        if (busy) return;
        busy = true;
        try { await UpdateAsync(); }
        catch (Exception) { media = null; } // A player closing mid-call invalidates its session; reconnect on the next tick.
        finally { busy = false; }
    }

    async Task UpdateAsync()
    {
        media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        var session = media.GetSessions()
            .Where(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
            .OrderByDescending(s => s.SourceAppUserModelId.Contains("Spotify", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (session is null) { Display("", ""); return; }

        var info = await session.TryGetMediaPropertiesAsync();
        var timeline = session.GetTimelineProperties();
        var duration = timeline.EndTime - timeline.StartTime;
        if (string.IsNullOrWhiteSpace(info.Title) || duration <= TimeSpan.Zero) { Display("", ""); return; }

        // Duration is part of the key: on a track change the title can arrive before the new duration.
        var playing = (info.Title, info.Artist ?? "", (int)duration.TotalSeconds);
        if (song != playing || retryAt <= DateTime.UtcNow)
        {
            song = playing;
            lines = null;
            retryAt = null;
            notice = "";
            _ = LoadAsync(playing, duration);
        }

        if (DateTime.UtcNow >= nextColorCheck)
        {
            nextColorCheck = DateTime.UtcNow + ColorInterval;
            _ = ApplyColorAsync(info.Thumbnail);
        }

        if (lines is not { Count: > 0 }) { Display(DateTime.UtcNow < noticeUntil ? notice : "", ""); return; }

        // Chrome rarely reports its position, so extrapolate from the last report.
        var rate = session.GetPlaybackInfo().PlaybackRate ?? 1;
        var position = timeline.Position + (DateTimeOffset.Now - timeline.LastUpdatedTime) * rate + Lead;
        var i = Lyrics.IndexAt(lines, position);
        Display(i >= 0 ? lines[i].Text : "", i + 1 < lines.Count ? lines[i + 1].Text : "");
        if (i >= 0) Sweep(Lyrics.Progress(lines, i, position, pace));
    }

    // Restarted every tick, so it follows seeks within a line.
    void Sweep((int Sung, TimeSpan Left) progress) =>
        sung.BeginAnimation(TextEffect.PositionCountProperty,
            new Int32Animation(progress.Sung, current.Text.Length, progress.Left));

    // Repeats while playing: the cover can arrive after the title on a track change.
    async Task ApplyColorAsync(IRandomAccessStreamReference? thumbnail)
    {
        var color = await Cover.ColorAsync(thumbnail) ?? Colors.Gold;
        if (((SolidColorBrush)sung.Foreground).Color != color) sung.Foreground = new SolidColorBrush(color);
    }

    async Task LoadAsync((string Title, string Artist, int Seconds) wanted, TimeSpan duration)
    {
        List<Line> found = [];
        string message;
        DateTime? retry = null;
        try
        {
            found = await Lyrics.FetchAsync(wanted.Title, wanted.Artist, duration);
            message = $"Lirik tidak ditemukan: {wanted.Title}";
        }
        catch (Exception)
        {
            message = "Gagal mengambil lirik. Dicoba lagi 30 detik lagi.";
            retry = DateTime.UtcNow + RetryDelay;
        }
        if (song != wanted) return;
        lines = found;
        pace = Lyrics.Pace(found);
        notice = message;
        noticeUntil = DateTime.UtcNow + NoticeTime;
        retryAt = retry;
    }

    void Display(string now, string after)
    {
        if (unlocked && now == "" && after == "") now = "Geser ke posisi yang diinginkan";
        if (shown == (now, after)) return;
        var wasEmpty = shown is null or ("", "");
        shown = (now, after);

        if (now == "" && after == "")
        {
            Animate(current, Transforms(current).Move.Y, -RollUp, current.Opacity, 0, fromScale: Transforms(current).Scale.ScaleX);
            Animate(next, Transforms(next).Move.Y, -RollUp, next.Opacity, 0,
                () => { if (shown == ("", "")) Visibility = Visibility.Hidden; });
            return;
        }

        // Positions include running transforms, so an interrupted roll continues from where it is.
        double leavingTop = TopOf(current), currentTop = TopOf(next);
        double leavingOpacity = current.Opacity, currentOpacity = next.Opacity;
        leaving.Text = current.Text;
        sung.BeginAnimation(TextEffect.PositionCountProperty, null);
        current.Text = now;
        next.Text = after;
        Visibility = Visibility.Visible;
        foreach (var label in new[] { leaving, current, next }) Reset(label);
        box.UpdateLayout();

        Animate(leaving, leavingTop - TopOf(leaving), leavingTop - TopOf(leaving) - RollUp, leavingOpacity, 0);
        Animate(current, wasEmpty ? RollIn : currentTop - TopOf(current), 0, wasEmpty ? 0 : currentOpacity, 1,
            fromScale: next.FontSize / current.FontSize);
        Animate(next, RollIn, 0, 0, NextOpacity);
    }

    double TopOf(UIElement label) => label.TranslatePoint(new Point(0, 0), box).Y;

    static (ScaleTransform Scale, TranslateTransform Move) Transforms(UIElement label)
    {
        var group = ((TransformGroup)label.RenderTransform).Children;
        return ((ScaleTransform)group[0], (TranslateTransform)group[1]);
    }

    static void Reset(UIElement label)
    {
        var (scale, move) = Transforms(label);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        move.BeginAnimation(TranslateTransform.YProperty, null);
    }

    static void Animate(UIElement label, double fromY, double toY, double fromOpacity, double toOpacity,
        Action? done = null, double fromScale = 1)
    {
        // Follows the Windows "Show animations" setting.
        var time = SystemParameters.ClientAreaAnimation ? RollTime : TimeSpan.Zero;
        DoubleAnimation Roll(double from, double to) =>
            new(from, to, time) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut } };

        var (scale, move) = Transforms(label);
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, Roll(fromScale, 1));
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, Roll(fromScale, 1));
        move.BeginAnimation(TranslateTransform.YProperty, Roll(fromY, toY));
        var fade = Roll(fromOpacity, toOpacity);
        if (done is not null) fade.Completed += (_, _) => done();
        label.BeginAnimation(OpacityProperty, fade);
    }
}

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest")) return Lyrics.SelfTest() && Cover.SelfTest() && Romanize.SelfTest() ? 0 : 1;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new Overlay();
        var unlock = new Forms.ToolStripMenuItem("Buka kunci / geser") { CheckOnClick = true };
        unlock.CheckedChanged += (_, _) => overlay.SetUnlocked(unlock.Checked);
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add(unlock);
        var lead = new Forms.ToolStripMenuItem();
        void Nudge(int milliseconds)
        {
            overlay.Lead += TimeSpan.FromMilliseconds(milliseconds);
            lead.Text = $"Lirik maju {overlay.Lead.TotalSeconds:0.0#} dtk";
        }
        lead.DropDownItems.Add("Lebih cepat 0,1 dtk", null, (_, _) => Nudge(100));
        lead.DropDownItems.Add("Lebih lambat 0,1 dtk", null, (_, _) => Nudge(-100));
        Nudge(0);
        menu.Items.Add(lead);
        menu.Items.Add("Keluar", null, (_, _) => app.Shutdown());
        using var tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Lirik Melayang",
            ContextMenuStrip = menu,
            Visible = true,
        };
        return app.Run(overlay);
    }
}
