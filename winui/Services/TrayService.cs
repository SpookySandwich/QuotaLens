using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using QuotaLens.Helpers;

namespace QuotaLens.Services;

/// <summary>
/// System-tray integration for QuotaLens, built on H.NotifyIcon's
/// <see cref="TaskbarIcon"/>. Ports the Tauri tray setup in
/// <c>src-tauri/src/main.rs</c> and PORT_SPEC §4.5:
///
///   Tooltip "QuotaLens". Context menu (in order): "Show Dashboard", "Refresh All",
///   separator, "Quit". Left-click toggles (shows/activates) the main window; it does
///   NOT open the menu (the menu is right-click only).
///
/// It also wires the close-to-tray behavior (PORT_SPEC §4.6): closing the main window
/// hides it to the tray instead of exiting. The owning <c>App</c> supplies the window
/// and the three action callbacks; this service owns no app state of its own.
///
/// Lifetime: create one instance, call <see cref="Initialize"/> once after the main
/// window exists, and <see cref="Dispose"/> on app shutdown (e.g. the Quit callback,
/// AFTER it has torn the rest of the app down).
///
/// Threading: callbacks (menu Click handlers, the left-click command) are invoked on
/// the UI/dispatcher thread, so they can touch the window directly.
/// </summary>
public sealed class TrayService : IDisposable
{
    private TaskbarIcon? _trayIcon;
    private Window? _window;
    private DispatcherQueue? _dispatcher;

    private Action? _onShow;
    private Action? _onRefreshAll;
    private Action? _onQuit;

    // Guards the close-to-tray interception so an explicit Quit can let the window
    // actually close instead of being cancelled back into hiding.
    private bool _allowClose;
    private bool _disposed;

    /// <summary>
    /// Build the tray icon + menu and wire the close-to-tray hook on the window.
    /// </summary>
    /// <param name="window">The main application window (label "main").</param>
    /// <param name="onShow">
    /// Invoked for "Show Dashboard" and tray left-click. Typically toggles the window
    /// via <see cref="WindowHelper.Toggle"/> (or <see cref="WindowHelper.Show"/> if you
    /// want the menu item to always raise it). The App owns this policy.
    /// </param>
    /// <param name="onRefreshAll">Invoked for "Refresh All" (fetch all providers + update UI).</param>
    /// <param name="onQuit">
    /// Invoked for "Quit". The App should perform real shutdown here (e.g.
    /// <c>RequestQuit()</c>: dispose this service, dispose <see cref="SingleInstance"/>,
    /// then <c>Application.Current.Exit()</c>). The tray will permit the subsequent
    /// window close instead of hiding it — see <see cref="AllowClose"/>.
    /// </param>
    public void Initialize(Window window, Action onShow, Action onRefreshAll, Action onQuit)
    {
        ArgumentNullException.ThrowIfNull(window);

        _window = window;
        _dispatcher = window.DispatcherQueue;
        _onShow = onShow;
        _onRefreshAll = onRefreshAll;
        _onQuit = onQuit;

        BuildTrayIcon();
        WireCloseToTray(window);
    }

    /// <summary>
    /// Allow the next window <c>Close</c> to proceed (i.e. NOT be intercepted into
    /// hide-to-tray). Call this from the App's quit path right before closing the
    /// window so the app can actually exit. The Quit menu item already does this.
    /// </summary>
    public void AllowClose() => _allowClose = true;

    private void BuildTrayIcon()
    {
        var showItem = new MenuFlyoutItem
        {
            Text = I18n.T("tray.showDashboard"),
            Command = new RelayCommand(() => Invoke(_onShow)),
        };

        var refreshItem = new MenuFlyoutItem
        {
            Text = I18n.T("common.refreshAll"),
            Command = new RelayCommand(() => Invoke(_onRefreshAll)),
        };

        var quitItem = new MenuFlyoutItem
        {
            Text = I18n.T("tray.quit"),
            Command = new RelayCommand(() =>
            {
                // PopupMenu mode executes MenuFlyoutItem.Command, not Click.
                // Permit App's real close path before it tears down the window.
                _allowClose = true;
                Invoke(_onQuit);
            }),
        };

        var menu = new MenuFlyout();
        menu.Items.Add(showItem);
        menu.Items.Add(refreshItem);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(quitItem);

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "QuotaLens",
            // App icon. ms-appx packaged asset (Assets/AppIcon.ico is Content in csproj).
            IconSource = new BitmapImage(new Uri("ms-appx:///Assets/AppIcon.ico")),
            // Right-click only shows the menu; left-click is handled by LeftClickCommand.
            // MenuActivation defaults to RightClick, which is exactly what we want
            // (Tauri: show_menu_on_left_click(false)), so it's left unset.
            ContextFlyout = menu,
            // Native popup menu (not a XAML second window) — matches the OS tray menu.
            ContextMenuMode = ContextMenuMode.PopupMenu,
            // No delay so the toggle feels responsive and isn't mistaken for a menu request.
            NoLeftClickDelay = true,
            // Left-click toggles the dashboard (Tauri: show_menu_on_left_click(false) + toggle_dashboard).
            LeftClickCommand = new RelayCommand(() => Invoke(_onShow)),
        };

        // ForceCreate makes the icon appear immediately even though it lives in code
        // (not in a XAML resource tree) and the window may start hidden.
        _trayIcon.ForceCreate(enablesEfficiencyMode: false);
    }

    private void WireCloseToTray(Window window)
    {
        // Intercept the AppWindow Closing event (the equivalent of Tauri's
        // WindowEvent::CloseRequested). Cancel the close and hide to tray, unless an
        // explicit Quit has set _allowClose.
        var appWindow = WindowHelper.GetAppWindow(window);
        appWindow.Closing += OnAppWindowClosing;
    }

    private void OnAppWindowClosing(Microsoft.UI.Windowing.AppWindow sender,
        Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_allowClose || _disposed || _window is null)
            return; // Let the close proceed (real shutdown).

        args.Cancel = true;          // Prevent destroy...
        WindowHelper.Hide(_window);  // ...and hide-to-tray instead.
    }

    private void Invoke(Action? action)
    {
        if (action is null)
            return;

        // Callbacks run on the UI thread. AppWindow.Closing / command execution are
        // already on it, but guard for safety if a future caller signals off-thread.
        if (_dispatcher is { } dq && !dq.HasThreadAccess)
            dq.TryEnqueue(() => action());
        else
            action();
    }

    /// <summary>
    /// Remove the tray icon and unhook the close handler. The OS removes the icon when
    /// the process exits, but calling this on a clean Quit avoids a lingering ghost icon.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _allowClose = true; // any pending close should now go through

        if (_window is not null)
        {
            try
            {
                WindowHelper.GetAppWindow(_window).Closing -= OnAppWindowClosing;
            }
            catch
            {
                // Window may already be torn down; ignore.
            }
        }

        _trayIcon?.Dispose();
        _trayIcon = null;
        _window = null;
    }
}
