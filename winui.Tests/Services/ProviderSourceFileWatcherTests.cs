using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class ProviderSourceFileWatcherTests
{
    [TestMethod]
    public void Change_BurstOfWrites_SignalsOnceAfterDebounce()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotelens-source-watch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var sessionPath = Path.Combine(directory, "session.json");
            File.WriteAllText(sessionPath, "{}");

            var signals = 0;
            using var signaled = new SemaphoreSlim(0);
            using var watcher = new ProviderSourceFileWatcher(
                sessionPath,
                () => { Interlocked.Increment(ref signals); signaled.Release(); },
                debounce: TimeSpan.FromMilliseconds(300),
                restartDelay: TimeSpan.FromMilliseconds(200));
            watcher.Start();

            for (var i = 0; i < 3; i++)
                File.WriteAllText(sessionPath, $$"""{ "n": {{i}} }""");

            Assert.IsTrue(signaled.Wait(TimeSpan.FromSeconds(10)), "no signal after session store rewrite");
            Thread.Sleep(1500);
            Assert.AreEqual(1, Volatile.Read(ref signals));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void Start_WithMissingDirectory_DoesNotThrowOrSignal()
    {
        using var watcher = new ProviderSourceFileWatcher(
            Path.Combine(Path.GetTempPath(), "quotalens-missing-" + Guid.NewGuid().ToString("N"), "session.json"),
            () => Assert.Fail("no signal expected without a session store"),
            debounce: TimeSpan.FromMilliseconds(100),
            restartDelay: TimeSpan.FromMilliseconds(100));
        watcher.Start();
    }
}
