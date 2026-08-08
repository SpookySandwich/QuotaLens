using System.Threading;

namespace QuotaLens.Services;

/// <summary>
/// Single-instance guard for QuotaLens (replaces the Tauri lock-file scheme in
/// <c>src-tauri/src/main.rs::ensure_single_instance</c>, see PORT_SPEC §4.9).
///
/// Uses a named system <see cref="Mutex"/> to detect whether another instance is
/// already running, plus a named <see cref="EventWaitHandle"/> so a second instance
/// can poke the first to bring its window to the front (the natural UX for a
/// tray app: launching it again "shows the dashboard" rather than spawning a clone).
///
/// Unlike the Rust lock file, an OS-owned named mutex is automatically released
/// when the owning process exits (even on a crash), so there is no stale-lock
/// reclaim path to worry about — the dead-PID handling from the Rust version is
/// unnecessary here.
///
/// Usage from <c>App</c> (composition root):
/// <code>
///   // Very early — in App's constructor or Main, BEFORE creating any window.
///   if (!SingleInstance.TryAcquire())
///   {
///       // Another instance owns the mutex. Tell it to show its window, then exit.
///       SingleInstance.SignalFirstInstance();
///       Process.GetCurrentProcess().Kill(); // or Environment.Exit(0)
///       return;
///   }
///   // We are the first instance. Listen for "show" signals from future launches:
///   SingleInstance.SecondInstanceRequested += (_, _) =>
///       _dispatcher.TryEnqueue(() => _windowHelper.BringToFront(_window));
///   // ... and call SingleInstance.Dispose() on app shutdown (e.g. tray Quit).
/// </code>
/// All members are static; the guard is process-wide.
/// </summary>
public static class SingleInstance
{
    // Globally-unique names. Not "Global\\" prefixed, so the scope is the current
    // user session — appropriate for a per-user tray app.
    private const string MutexName = "QuotaLens.SingleInstance";
    private const string EventName = "QuotaLens.SingleInstance.Show";

    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;
    private static Thread? _listenerThread;
    private static volatile bool _running;
    private static bool _ownsMutex;

    /// <summary>
    /// Raised on a background thread when another instance asks this (first) instance
    /// to show its window. Marshal to the UI thread before touching any window
    /// (e.g. <c>DispatcherQueue.TryEnqueue</c>).
    /// </summary>
    public static event EventHandler? SecondInstanceRequested;

    /// <summary>True if this process currently owns the single-instance mutex.</summary>
    public static bool IsFirstInstance => _ownsMutex;

    /// <summary>
    /// Attempt to become the single running instance. Returns <c>true</c> if this
    /// process acquired the mutex (it is the first/primary instance) and <c>false</c>
    /// if another instance already holds it.
    ///
    /// On success this also opens the shared "show" event and starts a background
    /// listener thread that raises <see cref="SecondInstanceRequested"/> whenever a
    /// later instance signals. Call this exactly once, as early as possible.
    /// </summary>
    public static bool TryAcquire()
    {
        if (_mutex is not null)
            return _ownsMutex; // Already attempted; report previous result.

        // createdNew is the authoritative signal: true only if WE created the mutex.
        _mutex = new Mutex(initiallyOwned: true, name: MutexName, createdNew: out var createdNew);
        _ownsMutex = createdNew;

        if (!createdNew)
        {
            // Another instance owns it. Release our (non-owning) handle and bail.
            _mutex.Dispose();
            _mutex = null;
            return false;
        }

        StartListener();
        return true;
    }

    /// <summary>
    /// Called by a SECOND instance (the one that failed <see cref="TryAcquire"/>) to
    /// ask the running first instance to surface its window. Safe to call even if no
    /// first instance is listening (the set is simply observed by nobody). The caller
    /// should exit immediately afterward.
    /// </summary>
    public static void SignalFirstInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(EventName, out var handle))
            {
                using (handle)
                    handle.Set();
            }
        }
        catch
        {
            // Best-effort: if signaling fails the second instance still just exits.
        }
    }

    private static void StartListener()
    {
        // Auto-reset: each Set() releases exactly one WaitOne() then re-blocks.
        _showEvent = new EventWaitHandle(initialState: false, EventResetMode.AutoReset, EventName);
        _running = true;

        _listenerThread = new Thread(ListenLoop)
        {
            IsBackground = true,
            Name = "QuotaLens.SingleInstance.Listener",
        };
        _listenerThread.Start();
    }

    private static void ListenLoop()
    {
        var handle = _showEvent;
        if (handle is null)
            return;

        while (_running)
        {
            // Wake periodically so Dispose() can stop us promptly without an extra signal.
            if (handle.WaitOne(500) && _running)
            {
                try
                {
                    SecondInstanceRequested?.Invoke(null, EventArgs.Empty);
                }
                catch
                {
                    // Never let a handler exception kill the listener thread.
                }
            }
        }
    }

    /// <summary>
    /// Releases the mutex and the listener. Call on normal app shutdown (e.g. the
    /// tray "Quit" path) for tidiness; the OS would release the mutex on process exit
    /// regardless.
    /// </summary>
    public static void Dispose()
    {
        _running = false;

        try { _listenerThread?.Join(1000); } catch { /* ignore */ }
        _listenerThread = null;

        _showEvent?.Dispose();
        _showEvent = null;

        if (_mutex is not null)
        {
            try
            {
                if (_ownsMutex)
                    _mutex.ReleaseMutex();
            }
            catch
            {
                // Mutex may already be abandoned/released; ignore.
            }
            _mutex.Dispose();
            _mutex = null;
        }

        _ownsMutex = false;
    }
}
