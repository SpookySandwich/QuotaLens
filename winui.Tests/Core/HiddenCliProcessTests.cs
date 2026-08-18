using System.Diagnostics;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class HiddenCliProcessTests
{
    [TestMethod]
    public void EnableUtf8StandardInput_UsesBomFreeEncoding()
    {
        var startInfo = new ProcessStartInfo();

        HiddenCliProcess.EnableUtf8StandardInput(startInfo);

        Assert.IsTrue(startInfo.RedirectStandardInput);
        var encoding = startInfo.StandardInputEncoding
            ?? throw new AssertFailedException("Redirected stdin must have an explicit encoding.");
        Assert.AreEqual(0, encoding.GetPreamble().Length);
        CollectionAssert.AreEqual(
            "{}\n"u8.ToArray(),
            encoding.GetBytes("{}\n"));
    }

    [TestMethod]
    public async Task BatchShim_IsResolvedAndRunsHeadlesslyWithStructuredArguments()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quotalens-hidden-cli-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var shim = Path.Combine(directory, "sample-cli.cmd");
        await File.WriteAllTextAsync(shim, "@echo off\r\necho \"%~1\"\r\necho \"%~2\"\r\n");

        try
        {
            var resolved = HiddenCliProcess.ResolveBinary(
                "sample-cli",
                new[] { directory },
                new[] { ".exe", ".cmd" });
            Assert.AreEqual(shim, resolved, ignoreCase: true);

            var startInfo = HiddenCliProcess.CreateStartInfo(
                resolved,
                new[] { "db", "select (1 & 2) from usage where provider = 'opencode-go'" });
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.CreateNoWindow);
            Assert.IsTrue(startInfo.RedirectStandardOutput);
            Assert.IsTrue(startInfo.RedirectStandardError);
            Assert.AreEqual(shim, startInfo.Environment["QUOTALENS_CLI_BINARY"]);
            Assert.AreEqual("2", startInfo.Environment["QUOTALENS_CLI_ARG_COUNT"]);
            CollectionAssert.AreEqual(
                new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand" },
                startInfo.ArgumentList.Take(4).ToArray());

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            Assert.AreEqual(0, process.ExitCode, stderr);
            StringAssert.Contains(stdout, "\"db\"");
            StringAssert.Contains(stdout, "\"select (1 & 2) from usage where provider = 'opencode-go'\"");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
