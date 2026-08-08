using Microsoft.UI.Xaml;
using Microsoft.Web.WebView2.Core;
using WinUIWebView2 = Microsoft.UI.Xaml.Controls.WebView2;

namespace QuotaLens.Views;

/// <summary>
/// Host window for a provider WebView login. It is a thin shell:
/// it exposes the embedded <see cref="WinUIWebView2"/> control so the
/// <c>WebLoginService</c> can drive CoreWebView2 creation, script injection and the
/// hash-poll loop. All WebView2 access happens on the UI thread (this window lives
/// on the DispatcherQueue the service was constructed with).
///
/// Mirrors the Tauri WebviewWindow built in open_bayesdl_login / open_mimo_login:
/// inner size 900x700, centered, decorations on. Visibility is controlled by the
/// service (hidden auto-fetch vs. visible manual login).
/// </summary>
public sealed partial class ProviderLoginWindow : Window
{
    public ProviderLoginWindow(string title)
    {
        InitializeComponent();
        Title = title;
        ResizeToDefault();
    }

    /// <summary>The embedded WebView2 control (CoreWebView2 created by the service).</summary>
    public WinUIWebView2 WebView => Web;

    /// <summary>Convenience accessor; null until EnsureCoreWebView2Async completes.</summary>
    public CoreWebView2? Core => Web.CoreWebView2;

    /// <summary>
    /// Park the window far off-screen for a HIDDEN auto-fetch. WinUI 3 still requires a
    /// window to be Activate()-d for its WebView2 to host the browser process and run the
    /// injected JS; the Tauri version used visible(false), but a never-activated WinUI
    /// window won't pump the WebView. Moving it off-screen keeps it invisible to the user
    /// while letting the page (and the scrape) run.
    /// </summary>
    public void MoveOffScreen()
    {
        try
        {
            var appWindow = AppWindow;
            if (appWindow is null) return;
            // Remove from taskbar + alt-tab so the silent fetch is invisible to the user.
            appWindow.IsShownInSwitchers = false;
            if (appWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter p)
            {
                p.IsMinimizable = false;
                p.IsMaximizable = false;
            }
            appWindow.Resize(new Windows.Graphics.SizeInt32(420, 360));
            appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        }
        catch
        {
            // Best-effort.
        }
    }

    /// <summary>
    /// Give provider login pages enough room to show their actual sign-in forms. Alibaba Cloud's
    /// login page, in particular, collapses to a marketing panel at narrow widths.
    /// </summary>
    private void ResizeToDefault()
    {
        try
        {
            var appWindow = AppWindow;
            if (appWindow is null) return;

            const int width = 1200;
            const int height = 800;
            appWindow.Resize(new Windows.Graphics.SizeInt32(width, height));

            // Center on the display containing the window.
            var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(
                appWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary);
            if (area is not null)
            {
                var work = area.WorkArea;
                var x = work.X + (work.Width - width) / 2;
                var y = work.Y + (work.Height - height) / 2;
                appWindow.Move(new Windows.Graphics.PointInt32(x, y));
            }
        }
        catch
        {
            // Sizing/centering is best-effort; never block the login flow on it.
        }
    }
}
