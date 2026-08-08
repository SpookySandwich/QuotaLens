using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace QuotaLens.Services;

/// <summary>
/// Win32/AppWindow helpers for the QuotaLens main window. Implements the window
/// behaviors from PORT_SPEC §4.6 and the Tauri <c>toggle_dashboard</c>/window logic
/// in <c>src-tauri/src/main.rs</c>: a compact dashboard window that starts hidden (tray app),
/// centers on the active monitor, and supports show / hide-to-tray / bring-to-front.
///
/// These are stateless helpers that operate on a <see cref="Window"/>. The
/// composition root (<c>App.xaml.cs</c>) owns the window instance and calls these.
///
/// Interop caveat: WinUI 3 windows do not expose an HWND directly. We obtain it via
/// <see cref="WindowNative.GetWindowHandle"/> and bridge to the windowing API with
/// <see cref="Win32Interop.GetWindowIdFromWindow"/> →
/// <see cref="AppWindow.GetFromWindowId"/>. All of these must run on the window's UI
/// thread.
/// </summary>
public static class WindowHelper
{
    /// <summary>Default dashboard size in logical pixels.</summary>
    public const int DefaultWidth = 620;
    public const int DefaultHeight = 720;

    private const int SW_HIDE = 0;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Native HWND of a WinUI <see cref="Window"/>.</summary>
    public static IntPtr GetHandle(Window window) => WindowNative.GetWindowHandle(window);

    /// <summary>The window's <see cref="AppWindow"/> (windowing-layer handle).</summary>
    public static AppWindow GetAppWindow(Window window)
    {
        var hwnd = GetHandle(window);
        var id = Win32Interop.GetWindowIdFromWindow(hwnd);
        return AppWindow.GetFromWindowId(id);
    }

    /// <summary>
    /// Resize the window to <see cref="DefaultWidth"/>×<see cref="DefaultHeight"/>.
    /// The values are scaled by the window's current DPI so the logical size remains
    /// stable regardless of monitor scaling.
    /// </summary>
    public static void SetDefaultSize(Window window) => SetSize(window, DefaultWidth, DefaultHeight);

    /// <summary>Resize to a logical (DPI-independent) width/height.</summary>
    public static void SetSize(Window window, int logicalWidth, int logicalHeight)
    {
        var appWindow = GetAppWindow(window);
        var scale = GetScaleForWindow(window);
        var w = (int)Math.Round(logicalWidth * scale);
        var h = (int)Math.Round(logicalHeight * scale);
        appWindow.Resize(new Windows.Graphics.SizeInt32(w, h));
    }

    /// <summary>
    /// Center the window on the monitor it currently sits on (matching Tauri
    /// <c>.center()</c>). Uses the work area so it does not overlap the taskbar.
    /// </summary>
    public static void CenterOnScreen(Window window)
    {
        var appWindow = GetAppWindow(window);
        var area = DisplayArea.GetFromWindowId(appWindow.Id, DisplayAreaFallback.Nearest);
        var work = area.WorkArea;

        var x = work.X + (work.Width - appWindow.Size.Width) / 2;
        var y = work.Y + (work.Height - appWindow.Size.Height) / 2;
        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    /// <summary>Convenience: apply the default size and center in one call.</summary>
    public static void SizeAndCenter(Window window)
    {
        SetDefaultSize(window);
        CenterOnScreen(window);
    }

    /// <summary>
    /// Show the window and bring it to the foreground. Also restores it if it was
    /// minimized (mirrors Tauri's show + unminimize + set_focus).
    /// </summary>
    public static void Show(Window window)
    {
        var appWindow = GetAppWindow(window);
        appWindow.Show();

        if (appWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();

        BringToFront(window);
    }

    /// <summary>
    /// Hide the window to the tray. Uses <see cref="AppWindow.Hide"/> which removes
    /// the window from screen and the taskbar without destroying it — the tray-app
    /// hide-to-tray behavior from PORT_SPEC §4.6.
    /// </summary>
    public static void Hide(Window window) => GetAppWindow(window).Hide();

    /// <summary>True if the window is currently shown on screen.</summary>
    public static bool IsVisible(Window window) => GetAppWindow(window).IsVisible;

    /// <summary>
    /// Bring an already-shown window to the front and give it focus. <see cref="Window.Activate"/>
    /// alone is unreliable for raising a background window, so we also call the Win32
    /// <c>SetForegroundWindow</c>.
    /// </summary>
    public static void BringToFront(Window window)
    {
        window.Activate();
        SetForegroundWindow(GetHandle(window));
    }

    /// <summary>
    /// Toggle visibility: if visible, hide-to-tray; otherwise show + activate.
    /// This is the direct port of Tauri's <c>toggle_dashboard</c> (tray left-click /
    /// "Show Dashboard"). See PORT_SPEC §4.5.
    /// </summary>
    public static void Toggle(Window window)
    {
        if (IsVisible(window))
            Hide(window);
        else
            Show(window);
    }

    private static double GetScaleForWindow(Window window)
    {
        try
        {
            // XamlRoot reflects the actual rasterization scale once content is loaded.
            var scale = (window.Content as Microsoft.UI.Xaml.FrameworkElement)?.XamlRoot?.RasterizationScale;
            if (scale is > 0)
                return scale.Value;
        }
        catch
        {
            // Fall through to DPI query below.
        }

        var dpi = Native.GetDpiForWindow(GetHandle(window));
        return dpi <= 0 ? 1.0 : dpi / 96.0;
    }

    private static class Native
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [System.Runtime.InteropServices.DefaultDllImportSearchPaths(System.Runtime.InteropServices.DllImportSearchPath.System32)]
        internal static extern uint GetDpiForWindow(IntPtr hwnd);
    }
}
