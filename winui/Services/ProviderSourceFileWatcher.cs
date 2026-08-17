using System.IO;

namespace QuotaLens.Services;

/// <summary>
/// Watches one provider-owned session file and coalesces write bursts into a
/// provider refetch. The provider declares the path; this mechanism has no
/// knowledge of provider identities or credential formats.
/// </summary>
public sealed class ProviderSourceFileWatcher : IDisposable
{
    private readonly string _path;
    private readonly Action _onChanged;
    private readonly TimeSpan _debounce;
    private readonly TimeSpan _restartDelay;
    private readonly object _gate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _restartTimer;
    private bool _disposed;

    public ProviderSourceFileWatcher(
        string path,
        Action onChanged,
        TimeSpan? debounce = null,
        TimeSpan? restartDelay = null)
    {
        _path = path;
        _onChanged = onChanged;
        _debounce = debounce ?? TimeSpan.FromSeconds(2);
        _restartDelay = restartDelay ?? TimeSpan.FromSeconds(5);
    }

    public void Start() => TryStartWatcher();

    private void OnFileTouched()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(_ => _onChanged(), null, _debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void TryStartWatcher()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            StopWatcherLocked();

            var directory = Path.GetDirectoryName(_path);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                _restartTimer = new Timer(_ => TryStartWatcher(), null, _restartDelay, Timeout.InfiniteTimeSpan);
                return;
            }

            var watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
            };
            watcher.Changed += (_, _) => OnFileTouched();
            watcher.Created += (_, _) => OnFileTouched();
            watcher.Renamed += (_, _) => OnFileTouched();
            watcher.Error += (_, _) => ScheduleRestart();
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
    }

    private void ScheduleRestart()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            StopWatcherLocked();
            _restartTimer = new Timer(_ => TryStartWatcher(), null, _restartDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void StopWatcherLocked()
    {
        _restartTimer?.Dispose();
        _restartTimer = null;
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _debounceTimer?.Dispose();
            StopWatcherLocked();
        }
    }
}
