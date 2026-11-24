using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;

namespace LirikMelayang;

// The normal window in the taskbar. Closing it closes the app; the lyrics float in Overlay.
class ControlWindow : Window
{
    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    readonly Overlay overlay;
    readonly VocalCut vocals;
    readonly string dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LirikMelayang");
    readonly TextBlock title = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock artist = new() { FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock lyricStatus = new() { Margin = new Thickness(0, 8, 0, 0), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock vocalStatus = new() { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    readonly TextBlock offset = new() { Width = 150, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    readonly RadioButton normal = new() { Content = "Suara asli", IsChecked = true };
    readonly RadioButton instrumental = new() { Content = "Kurangi vokal (instrumen saja)" };
    bool starting;

    string OffsetFile => Path.Combine(dataFolder, "offset.txt");

    public ControlWindow(Overlay overlay)
    {
        this.overlay = overlay;
        overlay.Vocals = vocals = new VocalCut(dataFolder);
        Title = "Lirik Melayang";
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ThemeMode = ThemeMode.System;
        // ThemeMode leaves the title bar light on Windows 10.
        SourceInitialized += (_, _) =>
        {
            var dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is 0 ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        };

        foreach (var label in new[] { lyricStatus, vocalStatus })
            AutomationProperties.SetLiveSetting(label, AutomationLiveSetting.Polite);

        var slower = new Button { Content = "Lebih lambat" };
        var faster = new Button { Content = "Lebih cepat" };
        slower.Click += (_, _) => Nudge(-100);
        faster.Click += (_, _) => Nudge(100);

        var unlock = new CheckBox { Content = "Geser lirik dengan mouse" };
        unlock.Checked += (_, _) => overlay.SetUnlocked(true);
        unlock.Unchecked += (_, _) => overlay.SetUnlocked(false);

        // Checked, not Click: screen readers select without clicking. Refresh selects too, but only the mode that is already on.
        normal.Checked += (_, _) => { if (vocals.Active) { vocals.Stop(); Say(""); } };
        instrumental.Checked += async (_, _) => await StartVocalCutAsync();

        Content = Stack(0,
            title, artist, lyricStatus,
            Section("Suara", normal, instrumental, vocalStatus),
            Section("Waktu lirik", new StackPanel { Orientation = Orientation.Horizontal, Children = { slower, offset, faster } }),
            Section("Posisi lirik", unlock));
        ((FrameworkElement)Content).Margin = new Thickness(24, 16, 24, 24);

        try { if (int.TryParse(File.ReadAllText(OffsetFile), out var saved)) overlay.Offset = TimeSpan.FromMilliseconds(saved); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // No saved setting yet.
        Nudge(0);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        timer.Tick += (_, _) => Refresh();
        timer.Start();
        Refresh();
        Closed += (_, _) => vocals.Stop();
    }

    static StackPanel Stack(double gap, params UIElement[] children)
    {
        var panel = new StackPanel();
        foreach (var child in children)
        {
            if (gap > 0) ((FrameworkElement)child).Margin = new Thickness(0, gap, 0, 0);
            panel.Children.Add(child);
        }
        return panel;
    }

    static StackPanel Section(string name, params UIElement[] children)
    {
        var panel = Stack(4, [new TextBlock { Text = name, FontWeight = FontWeights.SemiBold }, .. children]);
        panel.Margin = new Thickness(0, 24, 0, 0);
        return panel;
    }

    void Nudge(int milliseconds)
    {
        overlay.Offset += TimeSpan.FromMilliseconds(milliseconds);
        var seconds = overlay.Offset.TotalSeconds;
        offset.Text = seconds == 0 ? "Tepat" : $"{Math.Abs(seconds):0.0#} dtk {(seconds > 0 ? "lebih cepat" : "lebih lambat")}";
        if (milliseconds == 0) return;
        try
        {
            Directory.CreateDirectory(dataFolder);
            File.WriteAllText(OffsetFile, ((int)overlay.Offset.TotalMilliseconds).ToString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } // Still applies until the app closes.
    }

    async Task StartVocalCutAsync()
    {
        if (vocals.Active || starting) return;
        starting = true;
        normal.IsEnabled = instrumental.IsEnabled = false;
        try { Say(await vocals.StartAsync(overlay.PlayerId) ? "" : "Putar lagu dulu, lalu pilih lagi."); }
        catch (Exception e) { Say($"Vokal tidak bisa dikurangi: {e.Message}"); }
        starting = false;
        normal.IsEnabled = instrumental.IsEnabled = true;
        if (!IsLoaded) vocals.Stop(); // The window was closed meanwhile.
        ShowVocalMode();
    }

    void Refresh()
    {
        (title.Text, artist.Text, lyricStatus.Text) = overlay.Status;
        if (starting) return;
        var wasActive = vocals.Active;
        vocals.Check();
        if (wasActive && !vocals.Active) Say("Kembali ke suara asli karena volume pemutar atau perangkat suara berubah.");
        ShowVocalMode();
    }

    void ShowVocalMode() => (normal.IsChecked, instrumental.IsChecked) = (!vocals.Active, vocals.Active);

    void Say(string text)
    {
        vocalStatus.Text = text;
        vocalStatus.Visibility = text == "" ? Visibility.Collapsed : Visibility.Visible;
    }
}
