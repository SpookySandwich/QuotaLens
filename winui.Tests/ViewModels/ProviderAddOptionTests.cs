using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Helpers;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderAddOptionTests
{
    [TestMethod]
    public void Build_IncludesEveryAddableCatalogProviderOnce()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);

        CollectionAssert.AreEqual(
            Catalog.AddableTypes.Select(type => type.Id).OrderBy(id => id).ToArray(),
            options.Select(option => option.Id).OrderBy(id => id).ToArray());
    }

    [TestMethod]
    public void AddableTypes_ShowSingleAlibabaEntry()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);
        var alibabaIds = options
            .Where(option => option.Name.Contains("Alibaba", StringComparison.OrdinalIgnoreCase))
            .Select(option => option.Id)
            .ToArray();

        CollectionAssert.AreEqual(new[] { "alibaba" }, alibabaIds);
        Assert.IsTrue(Catalog.IsAddableProviderType("alibaba"));
        Assert.IsFalse(Catalog.IsAddableProviderType("alibabacloud"));
        Assert.IsFalse(Catalog.IsAddableProviderType("alibabatokenplan"));
    }

    [TestMethod]
    public void SetupKindFor_ClassifiesProviderSetupPatterns()
    {
        foreach (var providerType in new[] { "kimi", "deepseek", "qoder", "test-provider" })
            Assert.AreEqual(Catalog.SetupKindFor(providerType), ProviderAddOptions.SetupKindFor(providerType));
    }

    [TestMethod]
    public void CategoryLabel_IsDerivedFromStableLocalizationKey()
    {
        var option = new ProviderAddOption(Catalog.Types.First(type => type.Id == "kimi"), ProviderSetupKind.BrowserLogin);

        Assert.AreEqual("addProvider.setup.browserLogin", option.CategoryI18nKey);
        Assert.AreEqual("Browser login", option.CategoryLabel);
    }

    [TestMethod]
    public void Filter_MatchesNameIdAndSetupCategory()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);

        var byName = ProviderAddOptions.Filter(options, "openrouter");
        Assert.IsTrue(byName.Any(option => option.Id == "openrouter"));

        var byCategory = ProviderAddOptions.Filter(options, "browser login");
        Assert.IsTrue(byCategory.All(option => option.SetupKind == ProviderSetupKind.BrowserLogin));
        Assert.IsTrue(byCategory.Any(option => option.Id == "kimi"));
    }

    [TestMethod]
    public void Build_MarksProvidersTheUserAlreadyTracks()
    {
        var instances = new[]
        {
            new ProviderInstance("claude", "claude", "Claude Code"),
            new ProviderInstance("kimi", "kimi", "Kimi"),
            new ProviderInstance("kimi-work", "kimi", "Kimi (work)"),
        };

        var options = ProviderAddOptions.Build(Catalog.AddableTypes, instances);

        var claude = options.Single(option => option.Id == "claude");
        var kimi = options.Single(option => option.Id == "kimi");
        var cursor = options.Single(option => option.Id == "cursor");

        Assert.IsTrue(claude.IsAlreadyAdded);
        Assert.AreEqual("Added", claude.AddedBadgeText);
        // A second account of the same provider is legitimate, so the badge counts.
        Assert.AreEqual(2, kimi.AlreadyAddedCount);
        Assert.AreEqual("Added ×2", kimi.AddedBadgeText);
        Assert.IsFalse(cursor.IsAlreadyAdded);
        Assert.AreEqual("", cursor.AddedBadgeText);
    }

    [TestMethod]
    public void Build_WithoutInstances_MarksNothingAsAdded()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);

        Assert.IsTrue(options.All(option => !option.IsAlreadyAdded));
    }

    [TestMethod]
    public void GroupBySetupKind_OrdersBySetupRankAndOmitsEmptyKinds()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);

        var groups = ProviderAddOptions.GroupBySetupKind(options);

        CollectionAssert.AreEqual(
            new[] { "BrowserLogin", "ApiKey", "LocalAppOrCli" },
            groups.Select(group => group.Key).ToArray(),
            "Setup kinds must be listed in setup order, and kinds with no members must not appear.");
        Assert.IsTrue(groups.All(group => group.Count > 0));
        Assert.AreEqual(options.Count, groups.Sum(group => group.Count), "Every provider must land in exactly one group.");
        Assert.AreEqual("Browser login", groups[0].Label);
        Assert.AreEqual("Sign in once in a built-in browser window", groups[0].Hint);
    }

    [TestMethod]
    public void SuggestedGroup_KeepsAlreadyAddedAndCapsAtSix()
    {
        var all = ProviderAddOptions.Build(Catalog.AddableTypes);

        var suggested = ProviderAddOptions.SuggestedGroup(all);

        Assert.IsNotNull(suggested);
        Assert.IsTrue(suggested!.Count is > 0 and <= 6);
        Assert.IsTrue(suggested.Items.Any(option => option.Id == "claude"));

        var withClaudeAdded = ProviderAddOptions.Build(
            Catalog.AddableTypes,
            new[] { new ProviderInstance("claude", "claude", "Claude Code") });
        var kept = ProviderAddOptions.SuggestedGroup(withClaudeAdded);

        Assert.IsNotNull(kept);
        Assert.IsTrue(kept!.Items.Any(option => option.Id == "claude"),
            "Already-added common providers stay visible in Suggested.");
    }

    [TestMethod]
    public void FilterRanked_PutsTheMeantProviderFirst()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);

        // Exact/prefix beats substring: "cursor" over anything merely containing it.
        Assert.AreEqual("cursor", ProviderAddOptions.FilterRanked(options, "cursor")[0].Id);
        // Prefix beats mid-word: "open" leads with OpenAI/OpenCode/OpenRouter, not "Qoder".
        Assert.IsTrue(ProviderAddOptions.FilterRanked(options, "open")[0].Name
            .StartsWith("Open", StringComparison.OrdinalIgnoreCase));
        // Monogram equality is a first-class hit.
        Assert.IsTrue(ProviderAddOptions.FilterRanked(options, "Cx").Any(option => option.Id == "codex"));
    }

    [TestMethod]
    public void Rank_OrdersExactBeforePrefixBeforeWordBoundaryBeforeContains()
    {
        var options = ProviderAddOptions.Build(Catalog.AddableTypes);
        var codex = options.Single(option => option.Id == "codex");
        var commandCode = options.Single(option => option.Id == "commandcode");

        Assert.AreEqual(0, ProviderAddOptions.Rank(codex, "Codex"));
        Assert.AreEqual(1, ProviderAddOptions.Rank(codex, "Cod"));
        Assert.AreEqual(2, ProviderAddOptions.Rank(codex, "Cx"));
        // "Code" starts the second word of "Command Code" — better than a bare substring.
        Assert.AreEqual(3, ProviderAddOptions.Rank(commandCode, "Code"));
    }

    [TestMethod]
    public void AddedBadge_ShowsCheckForOneInstanceAndCountBeyond()
    {
        var type = Catalog.Types.First(t => t.Id == "claude");

        var once = new ProviderAddOption(type, ProviderSetupKind.LocalAppOrCli, 1);
        Assert.IsTrue(once.IsAlreadyAdded);
        Assert.IsTrue(once.ShowAddedCheck);
        Assert.IsFalse(once.ShowAddedCount);

        var thrice = new ProviderAddOption(type, ProviderSetupKind.LocalAppOrCli, 3);
        Assert.IsFalse(thrice.ShowAddedCheck);
        Assert.IsTrue(thrice.ShowAddedCount);
        Assert.AreEqual("3", thrice.AddedCountText);
        Assert.AreEqual("9+", new ProviderAddOption(type, ProviderSetupKind.LocalAppOrCli, 12).AddedCountText);

        var none = new ProviderAddOption(type, ProviderSetupKind.LocalAppOrCli);
        Assert.IsFalse(none.IsAlreadyAdded);
        Assert.AreEqual("", none.AddedCountText);
    }

    [TestMethod]
    public void AccessibleLabel_CarriesTheCategoryTheRowNoLongerPrints()
    {
        var type = Catalog.Types.First(t => t.Id == "kimi");

        var plain = new ProviderAddOption(type, ProviderSetupKind.BrowserLogin);
        Assert.AreEqual("Kimi — Browser login", plain.AccessibleLabel);
        Assert.AreEqual(plain.AccessibleLabel, plain.RowTooltip);

        var added = new ProviderAddOption(type, ProviderSetupKind.BrowserLogin, 2);
        StringAssert.Contains(added.AccessibleLabel, "Browser login");
        StringAssert.Contains(added.AccessibleLabel, "Added");
    }

    [TestMethod]
    public void VisualIdentity_UsesProviderThemeColorAndMonogram()
    {
        var codex = new ProviderAddOption(Catalog.Types.First(type => type.Id == "codex"), ProviderSetupKind.LocalAppOrCli);
        var qoder = new ProviderAddOption(Catalog.Types.First(type => type.Id == "qoder"), ProviderSetupKind.LocalAppOrCli);

        Assert.AreEqual("Cx", codex.Monogram);
        Assert.AreEqual(Brand.Color("codex"), Brand.Color(codex.Id));
        Assert.AreEqual("Q", qoder.Monogram);
        Assert.AreEqual(Brand.Color("qoder"), Brand.Color(qoder.Id));
    }
}
