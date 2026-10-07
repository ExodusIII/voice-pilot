using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace VoicePilot.App;

/// Small always-on-top pill at the bottom of the screen showing what VoicePilot is doing.
/// It never takes focus and lets clicks pass through, so typing into other apps keeps working.
public sealed class OverlayWindow : Window
{
    readonly TextBlock _title = new();
    readonly TextBlock _detail = new();
    readonly Border _card = new();
    readonly Rectangle[] _bars = new Rectangle[5];
    readonly Ellipse _dot = new();
    readonly DispatcherTimer _hide;
    readonly double[] _barShape = { 0.55, 0.85, 1.0, 0.8, 0.5 };
    readonly Random _rng = new();
    float _level;

    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);

    public OverlayWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "VoicePilot overlay";

        var bars = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Height = 26, Margin = new Thickness(0, 0, 14, 0) };
        for (var i = 0; i < _bars.Length; i++)
        {
            _bars[i] = new Rectangle { Width = 4, Height = 6, RadiusX = 2, RadiusY = 2, Margin = new Thickness(1.5, 0, 1.5, 0), VerticalAlignment = VerticalAlignment.Center };
            bars.Children.Add(_bars[i]);
        }
        _dot.Width = _dot.Height = 10;
        _dot.Margin = new Thickness(4, 0, 16, 0);
        _dot.VerticalAlignment = VerticalAlignment.Center;
        _dot.Visibility = Visibility.Collapsed;

        _title.FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI");
        _title.FontSize = 14;
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = new SolidColorBrush(Color.FromRgb(0xEE, 0xF0, 0xF5));
        _detail.FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        _detail.FontSize = 12.5;
        _detail.Foreground = new SolidColorBrush(Color.FromRgb(0xA9, 0xB0, 0xC0));
        _detail.TextTrimming = TextTrimming.CharacterEllipsis;
        _detail.MaxWidth = 460;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(_title);
        text.Children.Add(_detail);

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(bars);
        row.Children.Add(_dot);
        row.Children.Add(text);

        _card.Child = row;
        _card.Padding = new Thickness(18, 11, 22, 12);
        _card.CornerRadius = new CornerRadius(18);
        _card.MinWidth = 220;
        _card.Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x12, 0x15, 0x1C));
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        _card.BorderThickness = new Thickness(1);
        _card.Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.45, Color = Colors.Black };
        _card.Margin = new Thickness(24);
        Content = _card;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };

        var anim = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        anim.Tick += (_, _) => AnimateBars();
        anim.Start();

        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        SizeChanged += (_, _) => Reposition();
    }

    /// kind: listening | working | ok | error | confirm. autoHide: fade away after a moment (final results).
    public void ShowState(string title, string detail, string kind, bool autoHide)
    {
        var accent = kind switch
        {
            "listening" => Color.FromRgb(0x22, 0xD3, 0xEE),
            "ok" => Color.FromRgb(0x43, 0xDC, 0x9C),
            "error" => Color.FromRgb(0xFF, 0x75, 0x75),
            "confirm" => Color.FromRgb(0xF6, 0xBD, 0x55),
            _ => Color.FromRgb(0x9B, 0x8C, 0xFF),
        };
        var brush = new SolidColorBrush(accent);
        foreach (var b in _bars) b.Fill = brush;
        _dot.Fill = brush;
        var showBars = kind is "listening" or "working" or "confirm";
        foreach (var b in _bars) b.Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;
        ((Panel)_bars[0].Parent).Visibility = showBars ? Visibility.Visible : Visibility.Collapsed;
        _dot.Visibility = showBars ? Visibility.Collapsed : Visibility.Visible;
        _card.BorderBrush = new SolidColorBrush(Color.FromArgb(0x55, accent.R, accent.G, accent.B));

        _title.Text = title;
        _detail.Text = detail;
        _detail.Visibility = string.IsNullOrWhiteSpace(detail) ? Visibility.Collapsed : Visibility.Visible;
        _mode = kind;

        _hide.Stop();
        if (!IsVisible) Show();
        Reposition();
        if (autoHide) _hide.Start();
    }

    string _mode = "";

    public void SetLevel(float level) => _level = level;

    public void HideNow()
    {
        _hide.Stop();
        Hide();
    }

    void AnimateBars()
    {
        if (!IsVisible) return;
        for (var i = 0; i < _bars.Length; i++)
        {
            double h;
            if (_mode == "listening")
                h = 5 + 21 * Math.Min(1, _level * 1.3) * _barShape[i] * (0.75 + _rng.NextDouble() * 0.25);
            else
            {
                // Gentle wave while thinking/working.
                var t = Environment.TickCount64 / 160.0 + i * 0.9;
                h = 6 + 10 * (0.5 + 0.5 * Math.Sin(t)) * _barShape[i];
            }
            _bars[i].Height = h;
        }
    }

    void Reposition()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Bottom - ActualHeight - 8;
    }
}
