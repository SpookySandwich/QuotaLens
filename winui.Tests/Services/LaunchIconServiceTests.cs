using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class LaunchIconServiceTests
{
    [TestMethod]
    public void GetOrCreateIconPath_UsesExecutableResolvedFromConfiguredDirectory()
    {
        var target = new ProviderLaunchTarget(
            "Qoder",
            "qoder_app_path",
            Array.Empty<string>(),
            new[] { "QoderWork.exe" });
        var executable = @"C:\Program Files\QoderWork\QoderWork\QoderWork.exe";
        var cacheRoot = Path.Combine(Path.GetTempPath(), "QuotaLensIconTest", Guid.NewGuid().ToString("N"));

        try
        {
            var iconPath = LaunchIconService.GetOrCreateIconPath(
                "qoder",
                target,
                @"C:\Program Files\QoderWork\QoderWork",
                fileExists: path => path == executable || File.Exists(path),
                directoryExists: path => path == @"C:\Program Files\QoderWork\QoderWork",
                writeIconPng: (_, output) =>
                {
                    File.WriteAllText(output, "fake png");
                    return true;
                },
                cacheRoot);

            Assert.IsNotNull(iconPath);
            Assert.IsTrue(File.Exists(iconPath));
        }
        finally
        {
            if (Directory.Exists(cacheRoot))
                Directory.Delete(cacheRoot, recursive: true);
        }
    }

    [TestMethod]
    public void GetOrCreateIconPath_WhenLaunchExecutableCannotBeResolved_ReturnsNull()
    {
        var target = new ProviderLaunchTarget("Missing", null, new[] { @"C:\missing\App.exe" });

        var iconPath = LaunchIconService.GetOrCreateIconPath(
            "missing",
            target,
            customPath: null,
            fileExists: _ => false,
            directoryExists: _ => false,
            writeIconPng: (_, _) => throw new InvalidOperationException("Should not write"),
            cacheRoot: Path.GetTempPath());

        Assert.IsNull(iconPath);
    }
}
