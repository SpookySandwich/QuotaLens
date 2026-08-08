using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Providers;

namespace QuotaLens.Tests.Providers;

[TestClass]
public sealed class VertexAIProviderTests
{
    [TestMethod]
    public void ParseMonitoringQuota_WithMultipleRateQuotas_PreservesEveryMatchedSeries()
    {
        var usage = VertexAIProvider.ParseMonitoringQuota(
            TimeSeries(
                ("aiplatform.googleapis.com/generate_content_requests", "RequestsPerMinute", 40d),
                ("aiplatform.googleapis.com/generate_content_tokens", "TokensPerMinute", 800d)),
            TimeSeries(
                ("aiplatform.googleapis.com/generate_content_requests", "RequestsPerMinute", 100d),
                ("aiplatform.googleapis.com/generate_content_tokens", "TokensPerMinute", 1000d)));

        Assert.HasCount(2, usage.Quotas);
        Assert.AreEqual(80d, usage.RequestsUsedPercent, 0.001);

        var snapshot = VertexAIProvider.Snapshot(usage);
        Assert.AreEqual("generate_content_tokens · us-central1", snapshot.Primary.Label);
        Assert.IsNull(snapshot.Primary.WindowMinutes);
        Assert.HasCount(1, snapshot.AdditionalWindows);
        Assert.AreEqual("generate_content_requests · us-central1", snapshot.AdditionalWindows[0].Label);
    }

    [TestMethod]
    public void MergeTimeSeries_WithAllocationAndRateDocuments_KeepsBothCollections()
    {
        var merged = VertexAIProvider.MergeTimeSeries(
            TimeSeries(("allocation", "Allocation", 1d)),
            TimeSeries(("rate", "Rate", 2d)));

        var values = VertexAIProvider.AggregateTimeSeries(merged);

        Assert.HasCount(2, values);
    }

    [TestMethod]
    public void AggregateTimeSeries_WithHistoricSpike_UsesLatestPointInsteadOfMaximum()
    {
        var values = VertexAIProvider.AggregateTimeSeries("""
        {
          "timeSeries": [{
            "metric": {
              "labels": {
                "quota_metric": "aiplatform.googleapis.com/generate_content_requests",
                "limit_name": "RequestsPerMinute"
              }
            },
            "resource": { "labels": { "location": "us-central1" } },
            "points": [
              {
                "interval": { "endTime": "2026-08-03T12:00:00Z" },
                "value": { "doubleValue": 10 }
              },
              {
                "interval": { "endTime": "2026-08-03T11:00:00Z" },
                "value": { "doubleValue": 90 }
              }
            ]
          }]
        }
        """);

        Assert.AreEqual(10, values.Single().Value, 0.001);
    }

    [TestMethod]
    public void ParseMonitoringQuota_IgnoresStaleUsagePointFromSparseSeries()
    {
        var now = DateTimeOffset.Parse("2026-08-03T12:00:00Z");
        var usage = VertexAIProvider.ParseMonitoringQuota(
            """
            {
              "timeSeries": [
                {
                  "metric": { "labels": { "quota_metric": "stale", "limit_name": "RPM" } },
                  "resource": { "labels": { "location": "us-central1" } },
                  "points": [{
                    "interval": { "endTime": "2026-08-03T11:10:00Z" },
                    "value": { "doubleValue": 90 }
                  }]
                },
                {
                  "metric": { "labels": { "quota_metric": "fresh", "limit_name": "RPM" } },
                  "resource": { "labels": { "location": "us-central1" } },
                  "points": [{
                    "interval": { "endTime": "2026-08-03T11:55:00Z" },
                    "value": { "doubleValue": 25 }
                  }]
                }
              ]
            }
            """,
            """
            {
              "timeSeries": [
                {
                  "metric": { "labels": { "quota_metric": "stale", "limit_name": "RPM" } },
                  "resource": { "labels": { "location": "us-central1" } },
                  "points": [{ "value": { "doubleValue": 100 } }]
                },
                {
                  "metric": { "labels": { "quota_metric": "fresh", "limit_name": "RPM" } },
                  "resource": { "labels": { "location": "us-central1" } },
                  "points": [{ "value": { "doubleValue": 100 } }]
                }
              ]
            }
            """,
            now);

        Assert.HasCount(1, usage.Quotas);
        Assert.AreEqual("fresh · us-central1", usage.Quotas.Single().Label);
        Assert.AreEqual(25, usage.RequestsUsedPercent, 0.001);
    }

    private static string TimeSeries(params (string Metric, string Limit, double Value)[] rows)
    {
        var series = string.Join(
            ",",
            rows.Select(row => $$"""
            {
              "metric": {
                "labels": {
                  "quota_metric": "{{row.Metric}}",
                  "limit_name": "{{row.Limit}}"
                }
              },
              "resource": { "labels": { "location": "us-central1" } },
              "points": [{ "value": { "doubleValue": {{row.Value}} } }]
            }
            """));
        return $$"""{"timeSeries":[{{series}}]}""";
    }
}
