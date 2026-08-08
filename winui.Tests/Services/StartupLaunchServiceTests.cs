using Microsoft.VisualStudio.TestTools.UnitTesting;
using Microsoft.Win32;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class StartupLaunchServiceTests
{
    [TestMethod]
    public void BuildRunCommand_WithVisibleStartup_QuotesExecutablePath()
    {
        Assert.AreEqual(
            "\"C:\\Program Files\\QuotaLens\\QuotaLens.exe\"",
            StartupLaunchService.BuildRunCommand(@"C:\Program Files\QuotaLens\QuotaLens.exe", startHidden: false));
    }

    [TestMethod]
    public void BuildRunCommand_WithHiddenStartup_AppendsHiddenArgument()
    {
        Assert.AreEqual(
            "\"C:\\Program Files\\QuotaLens\\QuotaLens.exe\" --startup-hidden",
            StartupLaunchService.BuildRunCommand(@"C:\Program Files\QuotaLens\QuotaLens.exe", startHidden: true));
    }

    [TestMethod]
    [DataRow("--startup-hidden", true)]
    [DataRow("--STARTUP-HIDDEN", true)]
    [DataRow("--startup", false)]
    public void IsHiddenLaunch_WithCommandLineArgument_ReturnsExpectedResult(string argument, bool expected)
    {
        Assert.AreEqual(expected, StartupLaunchService.IsHiddenLaunch(["QuotaLens.exe", argument]));
    }

    [TestMethod]
    [DataRow("--ui-smoke", true)]
    [DataRow("--UI-SMOKE", true)]
    [DataRow("--startup-hidden", false)]
    public void IsUiSmokeLaunch_WithCommandLineArgument_ReturnsExpectedResult(string argument, bool expected)
    {
        Assert.AreEqual(expected, StartupLaunchService.IsUiSmokeLaunch(["QuotaLens.exe", argument]));
    }

    [TestMethod]
    public void AppLaunchPolicy_NormalLaunchEnablesInteractiveRuntimeServices()
    {
        var policy = AppLaunchPolicy.FromArguments(["QuotaLens.exe"]);

        Assert.IsTrue(policy.AcquireSingleInstance);
        Assert.IsTrue(policy.SignalExistingInstanceOnConflict);
        Assert.IsTrue(policy.CreateTray);
        Assert.IsTrue(policy.ActivateMainWindow);
        Assert.IsTrue(policy.StartRefresh);
    }

    [TestMethod]
    public void AppLaunchPolicy_HiddenStartupRefreshesWithoutActivatingWindow()
    {
        var policy = AppLaunchPolicy.FromArguments(["QuotaLens.exe", "--startup-hidden"]);

        Assert.IsTrue(policy.AcquireSingleInstance);
        Assert.IsFalse(policy.SignalExistingInstanceOnConflict);
        Assert.IsTrue(policy.CreateTray);
        Assert.IsFalse(policy.ActivateMainWindow);
        Assert.IsTrue(policy.StartRefresh);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AppLaunchPolicy_UiSmokeNeverStartsRefreshOrWebCapture(bool alsoStartHidden)
    {
        var arguments = alsoStartHidden
            ? new[] { "QuotaLens.exe", "--ui-smoke", "--startup-hidden" }
            : new[] { "QuotaLens.exe", "--ui-smoke" };

        var policy = AppLaunchPolicy.FromArguments(arguments);

        Assert.IsFalse(policy.AcquireSingleInstance);
        Assert.IsFalse(policy.SignalExistingInstanceOnConflict);
        Assert.IsFalse(policy.CreateTray);
        Assert.IsTrue(policy.ActivateMainWindow);
        Assert.IsFalse(policy.StartRefresh);
    }

    [TestMethod]
    public void SetEnabled_WritesAndRemovesCurrentUserRunValue()
    {
        var keyPath = $@"Software\QuotaLens.Tests\Startup\{Guid.NewGuid():N}";
        var service = new StartupLaunchService(
            keyPath,
            "QuotaLensTest",
            () => @"C:\Apps\QuotaLens.exe");

        try
        {
            Assert.IsFalse(service.IsEnabled());

            service.SetEnabled(true, startHidden: true);

            Assert.IsTrue(service.IsEnabled());
            Assert.IsTrue(service.IsStartHiddenEnabled());
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.AreEqual(
                    "\"C:\\Apps\\QuotaLens.exe\" --startup-hidden",
                    key?.GetValue("QuotaLensTest"));
            }

            service.SetEnabled(false, startHidden: true);

            Assert.IsFalse(service.IsEnabled());
            Assert.IsFalse(service.IsStartHiddenEnabled());
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath))
            {
                Assert.IsNull(key?.GetValue("QuotaLensTest"));
            }
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [TestMethod]
    public void SetEnabled_WithMissingProcessPath_Throws()
    {
        var service = new StartupLaunchService(
            $@"Software\QuotaLens.Tests\Startup\{Guid.NewGuid():N}",
            "QuotaLensTest",
            () => "");

        Assert.ThrowsExactly<InvalidOperationException>(() => service.SetEnabled(true, startHidden: true));
    }
}
