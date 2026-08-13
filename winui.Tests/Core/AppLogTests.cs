using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class AppLogTests
{
    [TestMethod]
    public void Initialize_WritesLinesAndTruncatesOnRestart()
    {
        var directory = TempDirectory();
        try
        {
            AppLog.Initialize(directory);
            AppLog.Info("first session line");
            AppLog.Warn("first session warning");

            var afterFirst = ReadAll(AppLog.FilePath!);
            StringAssert.Contains(afterFirst, "[INFO] first session line");
            StringAssert.Contains(afterFirst, "[WARN] first session warning");

            // A second Initialize (next app start) truncates: the old lines are gone.
            AppLog.Initialize(directory);
            AppLog.Info("second session line");

            var afterSecond = ReadAll(AppLog.FilePath!);
            StringAssert.Contains(afterSecond, "[INFO] second session line");
            Assert.IsFalse(afterSecond.Contains("first session line"));
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [TestMethod]
    public void Error_IncludesTheException()
    {
        var directory = TempDirectory();
        try
        {
            AppLog.Initialize(directory);
            AppLog.Error("fetch blew up", new System.InvalidOperationException("boom"));

            var text = ReadAll(AppLog.FilePath!);
            StringAssert.Contains(text, "[ERROR] fetch blew up");
            StringAssert.Contains(text, "InvalidOperationException");
            StringAssert.Contains(text, "boom");
        }
        finally
        {
            TryDelete(directory);
        }
    }

    [TestMethod]
    public void Initialize_WithUncreatableDirectory_FallsBackWithoutThrowing()
    {
        // A path that collides with an existing FILE cannot become the log
        // directory; Initialize must degrade to a no-op instead of throwing.
        var blocker = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "quotalens-tests",
            Guid.NewGuid().ToString("N") + ".blocker");
        System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(blocker)!);
        System.IO.File.WriteAllText(blocker, "file, not a directory");
        try
        {
            AppLog.Initialize(blocker);
            AppLog.Info("never written");
            Assert.IsNull(AppLog.FilePath);
        }
        finally
        {
            try { System.IO.File.Delete(blocker); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// Reads the live log the way a tail/editor would: sharing read access while
    /// the app keeps the file open for writing.
    /// </summary>
    private static string ReadAll(string path)
    {
        using var stream = new System.IO.FileStream(
            path,
            System.IO.FileMode.Open,
            System.IO.FileAccess.Read,
            System.IO.FileShare.ReadWrite);
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string TempDirectory()
    {
        var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "quotalens-tests", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string directory)
    {
        try
        {
            if (System.IO.Directory.Exists(directory))
                System.IO.Directory.Delete(directory, recursive: true);
        }
        catch
        {
            // Test cleanup is best-effort.
        }
    }
}
