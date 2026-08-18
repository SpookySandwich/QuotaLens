using System.Diagnostics;
using System.Runtime.InteropServices;

namespace QuotaLens.Services;

/// <summary>A visible top-level desktop window and the executable that owns it.</summary>
internal readonly record struct DesktopAppWindow(nint Handle, string ExecutablePath);

/// <summary>
/// Small native boundary used by background app launches. Keeping discovery and mutation
/// behind an interface makes the rule testable without launching or manipulating real apps.
/// </summary>
internal interface IDesktopWindowApi
{
    IReadOnlyList<DesktopAppWindow> VisibleTopLevelWindows();
    bool Hide(nint handle);
}

/// <summary>
/// Keeps windows created by one background launch hidden while the provider waits for the
/// app's local service. Windows that were already visible before the launch are never touched.
/// </summary>
internal sealed class BackgroundWindowSuppressor : IAsyncDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    private readonly string _executablePath;
    private readonly IDesktopWindowApi _windowApi;
    private readonly HashSet<nint> _preexistingVisibleWindows;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task? _suppressionLoop;
    private int _disposed;

    private BackgroundWindowSuppressor(
        string executablePath,
        IDesktopWindowApi windowApi,
        bool startLoop)
    {
        _executablePath = NormalizePath(executablePath);
        _windowApi = windowApi;
        _preexistingVisibleWindows = MatchingVisibleWindows()
            .Select(window => window.Handle)
            .ToHashSet();
        if (startLoop)
            _suppressionLoop = SuppressUntilDisposedAsync(_cts.Token);
    }

    public static BackgroundWindowSuppressor Start(string executablePath) =>
        new(executablePath, new Win32DesktopWindowApi(), startLoop: true);

    internal static BackgroundWindowSuppressor CreateForTest(
        string executablePath,
        IDesktopWindowApi windowApi) =>
        new(executablePath, windowApi, startLoop: false);

    /// <summary>Hides only windows created after this suppressor captured its baseline.</summary>
    internal int SuppressNewWindows()
    {
        var hidden = 0;
        foreach (var window in MatchingVisibleWindows())
        {
            if (_preexistingVisibleWindows.Contains(window.Handle))
                continue;
            if (_windowApi.Hide(window.Handle))
                hidden++;
        }
        return hidden;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        // Close the small race between the readiness probe succeeding and the polling
        // loop observing the app's last startup window.
        SuppressNewWindows();
        _cts.Cancel();
        if (_suppressionLoop is not null)
        {
            try
            {
                await _suppressionLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when a launch becomes ready or its dialog closes.
            }
        }
        _cts.Dispose();
    }

    private IEnumerable<DesktopAppWindow> MatchingVisibleWindows() =>
        _windowApi.VisibleTopLevelWindows().Where(window =>
            string.Equals(
                NormalizePath(window.ExecutablePath),
                _executablePath,
                StringComparison.OrdinalIgnoreCase));

    private async Task SuppressUntilDisposedAsync(CancellationToken ct)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            SuppressNewWindows();
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path.Trim().Trim('"'));
        }
        catch
        {
            return path.Trim().Trim('"');
        }
    }
}

/// <summary>
/// Lifetime returned by a desktop app launch. Disposing releases the process handle and,
/// for background launches, ends window suppression without terminating the launched app.
/// </summary>
internal sealed class DesktopAppLaunchSession(
    Process? process,
    BackgroundWindowSuppressor? windowSuppressor) : IAsyncDisposable
{
    public Process? Process { get; } = process;

    public async ValueTask DisposeAsync()
    {
        if (windowSuppressor is not null)
            await windowSuppressor.DisposeAsync().ConfigureAwait(false);
        Process?.Dispose();
    }
}

internal sealed class Win32DesktopWindowApi : IDesktopWindowApi
{
    private const int SwHide = 0;

    public IReadOnlyList<DesktopAppWindow> VisibleTopLevelWindows()
    {
        var windows = new List<DesktopAppWindow>();
        EnumWindows((handle, _) =>
        {
            if (!IsWindowVisible(handle))
                return true;

            GetWindowThreadProcessId(handle, out var processId);
            if (processId == 0)
                return true;

            try
            {
                using var process = Process.GetProcessById(unchecked((int)processId));
                var path = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(path))
                    windows.Add(new DesktopAppWindow(handle, path));
            }
            catch
            {
                // Processes can exit or deny module inspection during enumeration.
            }
            return true;
        }, nint.Zero);
        return windows;
    }

    public bool Hide(nint handle) => ShowWindowAsync(handle, SwHide);

    private delegate bool EnumWindowsCallback(nint window, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsCallback callback, nint parameter);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindowAsync(nint window, int command);
}
