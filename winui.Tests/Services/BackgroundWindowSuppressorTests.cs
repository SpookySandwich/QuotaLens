using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Services;

namespace QuotaLens.Tests.Services;

[TestClass]
public sealed class BackgroundWindowSuppressorTests
{
    [TestMethod]
    public async Task SuppressNewWindows_HidesOnlyNewWindowsOwnedByLaunchedExecutable()
    {
        const string target = @"C:\Apps\Antigravity\Antigravity.exe";
        var windows = new FakeDesktopWindowApi(
            new DesktopAppWindow((nint)1, target),
            new DesktopAppWindow((nint)2, @"C:\Apps\Other\Other.exe"));
        await using var suppressor = BackgroundWindowSuppressor.CreateForTest(target, windows);

        windows.Add(
            new DesktopAppWindow((nint)3, target.ToUpperInvariant()),
            new DesktopAppWindow((nint)4, @"C:\Apps\Other\Other.exe"));

        var hidden = suppressor.SuppressNewWindows();

        Assert.AreEqual(1, hidden);
        CollectionAssert.AreEqual(new[] { (nint)3 }, windows.Hidden.ToArray());
    }

    [TestMethod]
    public async Task SuppressNewWindows_DoesNotHideAWindowThatWasVisibleBeforeLaunch()
    {
        const string target = @"C:\Apps\Antigravity IDE\Antigravity IDE.exe";
        var windows = new FakeDesktopWindowApi(new DesktopAppWindow((nint)40, target));
        await using var suppressor = BackgroundWindowSuppressor.CreateForTest(target, windows);

        var hidden = suppressor.SuppressNewWindows();

        Assert.AreEqual(0, hidden);
        Assert.AreEqual(0, windows.Hidden.Count);
    }

    private sealed class FakeDesktopWindowApi(params DesktopAppWindow[] initial) : IDesktopWindowApi
    {
        private readonly List<DesktopAppWindow> _windows = new(initial);
        public List<nint> Hidden { get; } = new();

        public void Add(params DesktopAppWindow[] windows) => _windows.AddRange(windows);

        public IReadOnlyList<DesktopAppWindow> VisibleTopLevelWindows() =>
            _windows.Where(window => !Hidden.Contains(window.Handle)).ToArray();

        public bool Hide(nint handle)
        {
            Hidden.Add(handle);
            return true;
        }
    }
}
