using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class SnapshotStoreTests
{
    [TestMethod]
    public void RoundTripsHealthySnapshotsAndNeverPersistsErrors()
    {
        var dir = Path.Combine(Path.GetTempPath(), "quotalens-snapshots-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new SnapshotStore(dir);

            // No cache initially.
            Assert.IsNull(store.Load("grok-abc", "grok"));

            // Save + load a healthy snapshot.
            store.Save("grok-abc", "grok", new ProviderSnapshot
            {
                ProviderId = "grok-abc",
                Name = "Grok",
                Primary = new RateWindow { Label = "Weekly included", UsedPercent = 42.5 },
                SourceState = new ProviderSourceState("app", "cli", UsedFallback: true),
                UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            });

            var loaded = store.Load("grok-abc", "grok");
            Assert.IsNotNull(loaded);
            Assert.AreEqual("Weekly included", loaded!.Primary.Label);
            Assert.AreEqual(42.5, loaded.Primary.UsedPercent);
            Assert.AreEqual("cli", loaded.SourceState?.EffectiveSourceId);
            Assert.IsTrue(loaded.SourceState!.UsedFallback);

            // An error snapshot must NOT overwrite the last good data.
            store.Save("grok-abc", "grok", new ProviderSnapshot
            {
                ProviderId = "grok-abc",
                Primary = new RateWindow { Label = "Error", UsedPercent = 0 },
                Error = "Login required",
            });
            var stillGood = store.Load("grok-abc", "grok");
            Assert.IsNotNull(stillGood);
            Assert.AreEqual("Weekly included", stillGood!.Primary.Label);

            // Wrong provider type must not match the cached envelope.
            Assert.IsNull(store.Load("grok-abc", "gemini"));

            store.Delete("grok-abc");
            Assert.IsNull(store.Load("grok-abc", "grok"));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
