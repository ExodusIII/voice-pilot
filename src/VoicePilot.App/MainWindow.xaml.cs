using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using VoicePilot.Core;

namespace VoicePilot.App;

public partial class MainWindow : Window
{
    public AppHost Host { get; }
    readonly string? _startView;
    bool _exiting;
    bool _webStarted;

    public MainWindow(bool startHidden, string? startView = null)
    {
        InitializeComponent();
        _startView = startView is "home" or "commands" or "settings" ? startView : null;
        Host = new AppHost(this);
        RestoreBounds_();
        SourceInitialized += (_, _) =>
        {
            Host.ApplyTheme();
            Host.AttachHotkey(new WindowInteropHelper(this).Handle);
        };
        Loaded += async (_, _) => await StartWebAsync();
        Closing += OnClosing;
        if (startHidden)
        {
            // Create the window handle (for the hotkey) without showing it.
            new WindowInteropHelper(this).EnsureHandle();
        }
        else Show();
        Host.Start();
    }

    async Task StartWebAsync()
    {
        if (_webStarted) return;
        _webStarted = true;
        try
        {
            Web.DefaultBackgroundColor = Host.IsDark ? System.Drawing.Color.FromArgb(10, 12, 16) : System.Drawing.Color.FromArgb(246, 247, 250);
            var dataDir = Path.Combine(Paths.Local, "WebView2");
            var env = await CoreWebView2Environment.CreateAsync(null, dataDir);
            await Web.EnsureCoreWebView2Async(env);

            var core = Web.CoreWebView2;
            core.SetVirtualHostNameToFolderMapping(Bridge.Host, Path.Combine(AppContext.BaseDirectory, "web"), CoreWebView2HostResourceAccessKind.DenyCors);
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
#if !DEBUG
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
#endif
            // The UI never navigates away; web links open in the default browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; OpenExternal(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith(Bridge.Origin, StringComparison.OrdinalIgnoreCase)) return;
                e.Cancel = true;
                OpenExternal(e.Uri);
            };
            Host.Bridge.Attach(core);
            core.Navigate(Bridge.Origin + "index.html" + (_startView is null ? "" : "?view=" + _startView));
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this, "Microsoft Edge WebView2 Runtime was not found.\nInstall it from https://developer.microsoft.com/microsoft-edge/webview2 and start VoicePilot again.",
                "VoicePilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            Log.Error("web view failed to start", ex);
            MessageBox.Show(this, "The VoicePilot window could not be loaded:\n" + ex.Message, "VoicePilot", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static void OpenExternal(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            Process.Start(new ProcessStartInfo(u.AbsoluteUri) { UseShellExecute = true });
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void ExitApp()
    {
        _exiting = true;
        Close();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveBounds();
        if (!_exiting && Host.Settings.Value.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            Host.ShowTrayHintOnce();
            return;
        }
        Host.Dispose();
        Application.Current.Shutdown();
    }

    void RestoreBounds_()
    {
        var b = Host.Settings.Value.Window;
        if (b is null || b.Width < MinWidth || b.Height < MinHeight) return;
        var onScreen = b.Left >= SystemParameters.VirtualScreenLeft - 20
            && b.Top >= SystemParameters.VirtualScreenTop - 20
            && b.Left + b.Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 20
            && b.Top + b.Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 20;
        if (!onScreen) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = b.Left;
        Top = b.Top;
        Width = b.Width;
        Height = b.Height;
        if (b.Maximized) WindowState = WindowState.Maximized;
    }

    void SaveBounds()
    {
        var r = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (r.IsEmpty || r.Width < 100) return;
        Host.Settings.Value.Window = new WindowBounds
        {
            Left = r.Left,
            Top = r.Top,
            Width = r.Width,
            Height = r.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
        try { Host.Settings.Save(); } catch { }
    }
}
