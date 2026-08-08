using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens;

/// <summary>Application composition root: wires config, providers, refresh, tray, window.</summary>
public partial class App : Application
{
    private Window? _window;
    private TrayService? _tray;
    private RefreshService? _svc;
    private bool _isQuitting;

    public App()
    {
        InitializeComponent();
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Headless self-test: fetch the API providers and write results to a file.
        var cmd = Environment.GetCommandLineArgs();
        var launchPolicy = AppLaunchPolicy.FromArguments(cmd);
        if (cmd.Any(a => a.Equals("--selftest", StringComparison.OrdinalIgnoreCase)))
        {
            await RunSelfTestAsync();
            Exit();
            return;
        }

        if (launchPolicy.AcquireSingleInstance && !SingleInstance.TryAcquire())
        {
            if (launchPolicy.SignalExistingInstanceOnConflict)
                SingleInstance.SignalFirstInstance();
            Exit();
            return;
        }

        var ui = DispatcherQueue.GetForCurrentThread();
        WebLoginService.Instance = new WebLoginService(ui);

        var config = new ConfigService();
        _svc = new RefreshService(config, ui);

        _window = new MainWindow(_svc);
        WindowHelper.SizeAndCenter(_window);

        if (launchPolicy.CreateTray)
        {
            SingleInstance.SecondInstanceRequested += (_, _) =>
                ui.TryEnqueue(() => WindowHelper.Show(_window!));

            _tray = new TrayService();
            _tray.Initialize(
                _window,
                onShow: () => WindowHelper.Toggle(_window!),
                onRefreshAll: () => { _ = _svc!.RefreshAllAsync(); },
                onQuit: RequestQuit);
        }

        if (launchPolicy.ActivateMainWindow)
            _window.Activate();

        if (launchPolicy.StartRefresh)
        {
            _svc.StartAutoRefresh();
            _ = _svc.RefreshAllAsync();
        }
    }

    private void RequestQuit()
    {
        if (_isQuitting)
            return;

        _isQuitting = true;

        var tray = _tray;
        _tray = null;
        tray?.AllowClose();
        tray?.Dispose();

        SingleInstance.Dispose();

        try
        {
            _window?.Close();
        }
        catch
        {
            // Shutdown should continue even if the window is already gone.
        }
        _window = null;

        Exit();
        Environment.Exit(0);
    }

    private static async Task RunSelfTestAsync()
    {
        var sb = new StringBuilder();
        try
        {
            var config = new ConfigService();
            string[] fallbackTypes = { "codex-lb", "deepseek", "alibabacloud", "claude", "antigravity", "kiro", "qoder" };
            var targets = config.Instances
                .Select(instance => (InstanceId: instance.Id, Type: instance.Type))
                .ToList();
            foreach (var type in fallbackTypes)
            {
                if (!targets.Any(target => string.Equals(target.Type, type, StringComparison.OrdinalIgnoreCase)))
                    targets.Add((type, type));
            }

            foreach (var (instanceId, type) in targets)
            {
                try
                {
                    var p = ProviderRegistry.Create(type);
                    var snap = await p.FetchAsync(instanceId, config, CancellationToken.None);
                    var bal = snap.Balance != null ? $"{snap.Balance.Currency} {snap.Balance.Total}" : "-";
                    sb.AppendLine($"[OK] {instanceId} ({type}): name='{snap.Name}' primary='{snap.Primary.Label}' used={snap.Primary.UsedPercent:F1}% balance={bal} error={snap.Error ?? "none"}");
                }
                catch (Exception ex)
                {
                    sb.AppendLine($"[ERR] {instanceId} ({type}): {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("FATAL: " + ex);
        }
        try
        {
            var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotalens_selftest.txt");
            await System.IO.File.WriteAllTextAsync(path, sb.ToString());
        }
        catch { }
    }
}
