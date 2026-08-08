using System.Text.Json;
using System.Text.RegularExpressions;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderUpstreamLockTests
{
    private const string ExpectedPurpose = "codexbar-upstream-compatibility";
    private const string ExpectedRepository = "https://github.com/steipete/CodexBar";
    private const string ExpectedRevision = "8ef86077e70ac27d45ddddaf49e409824ccdf668";
    private const string ExpectedProviderListPath = "Sources/CodexBarCore/Providers/Providers.swift";
    private const string ExpectedProviderListSha256 = "d9726f9dcc52d82ead6899c34054b3cfd030dcc7010e342da68eba529daf7d0f";
    private const string ExpectedImplementationRegistryPath = "Sources/CodexBar/Providers/Shared/ProviderImplementationRegistry.swift";
    private const string ExpectedImplementationRegistrySha256 = "cc712f26c420be1686fbc2bbc04fee93285b471c588995fc4b64c0a31b7c3188";
    private const string ExpectedAppRegistryPath = "Sources/CodexBar/ProviderRegistry.swift";
    private const string ExpectedAppRegistrySha256 = "fd8e15b85db6ca50cd8ef2bcea95b15da51763f2e55d3ad9347c76bf86d68fea";
    private const string ExpectedCatalogPath = "winui/Core/Catalog.cs";
    private const int ExpectedProviderCount = 66;

    [TestMethod]
    public void LockMetadata_PinsReproducibleCompatibilitySources()
    {
        // Arrange
        using var document = LoadLock();
        var root = document.RootElement;
        var upstream = root.GetProperty("upstream");

        // Act
        var scopeNote = root.GetProperty("scopeNote").GetString();
        var revision = upstream.GetProperty("baselineRevision").GetString();

        // Assert
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(ExpectedPurpose, root.GetProperty("purpose").GetString());
        StringAssert.Contains(scopeNote, "not official provider evidence", StringComparison.OrdinalIgnoreCase);
        Assert.AreEqual(ExpectedRepository, upstream.GetProperty("repository").GetString());
        Assert.AreEqual(ExpectedRevision, revision);
        Assert.AreEqual(ProviderContracts.AuditedUpstreamRevision, revision);
        Assert.IsTrue(Regex.IsMatch(revision!, "^[0-9a-f]{40}$", RegexOptions.CultureInvariant));
        AssertTrackedSource(
            upstream,
            "providerListPath",
            "providerListSha256",
            ExpectedProviderListPath,
            ExpectedProviderListSha256);
        AssertTrackedSource(
            upstream,
            "implementationRegistryPath",
            "implementationRegistrySha256",
            ExpectedImplementationRegistryPath,
            ExpectedImplementationRegistrySha256);
        AssertTrackedSource(
            upstream,
            "appRegistryPath",
            "appRegistrySha256",
            ExpectedAppRegistryPath,
            ExpectedAppRegistrySha256);
    }

    [TestMethod]
    public void ProviderIds_AreCompleteSortedAndUniqueForPinnedRevision()
    {
        // Arrange
        using var document = LoadLock();
        var root = document.RootElement;

        // Act
        var providerIds = ReadStringArray(root, "providerIds");
        var sortedProviderIds = providerIds.Order(StringComparer.Ordinal).ToArray();
        var distinctProviderIds = providerIds.Distinct(StringComparer.Ordinal).ToArray();

        // Assert
        Assert.AreEqual(ExpectedProviderCount, root.GetProperty("providerCount").GetInt32());
        Assert.AreEqual(ExpectedProviderCount, providerIds.Length);
        CollectionAssert.AreEqual(sortedProviderIds, providerIds, "Provider IDs must use ordinal sort order.");
        CollectionAssert.AreEqual(distinctProviderIds, providerIds, "Provider IDs must be unique.");
        Assert.IsTrue(
            providerIds.All(id => Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)),
            "Provider IDs must be lowercase canonical identifiers.");
    }

    [TestMethod]
    public void QuotaLensRelationship_ClassifiesEveryCatalogDifferenceExplicitly()
    {
        // Arrange
        using var document = LoadLock();
        var root = document.RootElement;
        var relationship = root.GetProperty("quotaLensRelationship");
        var upstreamIds = ReadStringArray(root, "providerIds");
        var catalogIds = Catalog.Types
            .Select(type => type.Id)
            .Order(StringComparer.Ordinal)
            .ToArray();

        // Act
        var actualQuotaLensOnly = catalogIds
            .Except(upstreamIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var actualUpstreamOnly = upstreamIds
            .Except(catalogIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var sharedIds = catalogIds
            .Intersect(upstreamIds, StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var expectedQuotaLensOnly = ReadStringArray(relationship, "quotaLensOnlyIds");
        var expectedUpstreamOnly = ReadStringArray(relationship, "upstreamOnlyIds");

        // Assert
        Assert.AreEqual(ExpectedCatalogPath, relationship.GetProperty("catalogPath").GetString());
        Assert.AreEqual(catalogIds.Length, relationship.GetProperty("catalogCount").GetInt32());
        Assert.AreEqual(sharedIds.Length, relationship.GetProperty("sharedCount").GetInt32());
        Assert.IsTrue(sharedIds.Length >= 40, "The pinned compatibility source should have meaningful overlap with QuotaLens.");
        AssertSortedUniqueLowercase(expectedQuotaLensOnly, "quotaLensOnlyIds");
        AssertSortedUniqueLowercase(expectedUpstreamOnly, "upstreamOnlyIds");
        CollectionAssert.AreEqual(expectedQuotaLensOnly, actualQuotaLensOnly);
        CollectionAssert.AreEqual(expectedUpstreamOnly, actualUpstreamOnly);
    }

    private static void AssertTrackedSource(
        JsonElement upstream,
        string pathProperty,
        string hashProperty,
        string expectedPath,
        string expectedHash)
    {
        var path = upstream.GetProperty(pathProperty).GetString();
        var hash = upstream.GetProperty(hashProperty).GetString();

        Assert.AreEqual(expectedPath, path);
        Assert.AreEqual(expectedHash, hash);
        Assert.IsTrue(Regex.IsMatch(hash!, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant));
    }

    private static void AssertSortedUniqueLowercase(string[] ids, string propertyName)
    {
        var sorted = ids.Order(StringComparer.Ordinal).ToArray();
        var distinct = ids.Distinct(StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(sorted, ids, $"{propertyName} must use ordinal sort order.");
        CollectionAssert.AreEqual(distinct, ids, $"{propertyName} must contain unique IDs.");
        Assert.IsTrue(
            ids.All(id => Regex.IsMatch(id, "^[a-z0-9][a-z0-9-]*$", RegexOptions.CultureInvariant)),
            $"{propertyName} must contain lowercase canonical identifiers.");
    }

    private static JsonDocument LoadLock()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "provider-upstream-lock.json");
            if (File.Exists(candidate))
                return JsonDocument.Parse(File.ReadAllText(candidate));
        }

        throw new FileNotFoundException("Could not locate provider-upstream-lock.json from the test output directory.");
    }

    private static string[] ReadStringArray(JsonElement parent, string propertyName) =>
        parent.GetProperty(propertyName)
            .EnumerateArray()
            .Select(element => element.GetString() ?? throw new InvalidDataException($"{propertyName} contains null."))
            .ToArray();
}
