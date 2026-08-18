using System.Text.RegularExpressions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderSnapshotIdentityTests
{
    [TestMethod]
    [DataRow("grok", "Grok", "X Premium+", "Grok · X Premium+")]
    [DataRow("kimi", "Kimi", "Allegro", "Kimi · Allegro")]
    [DataRow("gemini", "Gemini", "AI Pro", "Gemini · AI Pro")]
    [DataRow("claude", "Work", "Max", "Work · Max")]
    public void ComposeTitle_UsesTheSameFormatForEveryProvider(
        string providerType,
        string instanceName,
        string planName,
        string expected)
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = providerType,
            Name = Catalog.ProviderName(providerType),
            PlanName = planName,
        };

        Assert.AreEqual(
            expected,
            ProviderSnapshotIdentity.ComposeTitle(providerType, instanceName, snapshot));
    }

    [TestMethod]
    public void Normalize_UpgradesLegacyTitleToStructuredPlanIdentity()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "kimi",
            Name = "Kimi · Moderato",
        };

        ProviderSnapshotIdentity.Normalize("kimi", snapshot);

        Assert.AreEqual("Moderato", snapshot.PlanName);
        Assert.AreEqual("Kimi · Moderato", snapshot.Name);
    }

    [TestMethod]
    public void Normalize_ExpiredEntitlementNeverDisplaysAPlan()
    {
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = "MiMo",
            PlanId = "standard",
            PlanName = "Standard",
            EntitlementStatus = EntitlementStatus.Expired,
        };

        ProviderSnapshotIdentity.Normalize("mimo", snapshot);

        Assert.AreEqual("MiMo", snapshot.Name);
        Assert.IsNull(snapshot.PlanId);
        Assert.IsNull(snapshot.PlanName);
    }

    [TestMethod]
    public void ProviderParsers_DoNotComposePresentationTitles()
    {
        var repository = FindRepositoryRoot();
        var files = Directory.EnumerateFiles(
                Path.Combine(repository, "winui", "Providers"),
                "*.cs",
                SearchOption.TopDirectoryOnly)
            .Append(Path.Combine(repository, "winui", "Services", "WebLoginService.cs"));
        var violations = new List<string>();

        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            foreach (Match match in Regex.Matches(
                         source,
                         @"\b(?:[A-Za-z_][A-Za-z0-9_]*\.)?Name\s*=\s*(?<expression>[^,;]+)",
                         RegexOptions.CultureInvariant))
            {
                if (match.Groups["expression"].Value.Contains(" · ", StringComparison.Ordinal))
                {
                    var line = source.Take(match.Index).Count(character => character == '\n') + 1;
                    violations.Add($"{Path.GetRelativePath(repository, file)}:{line}");
                }
            }
        }

        Assert.AreEqual(
            0,
            violations.Count,
            "Providers must set structured PlanId/PlanName; only ProviderSnapshotIdentity composes titles: "
            + string.Join(", ", violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "QuotaLens.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the QuotaLens repository root.");
    }
}
