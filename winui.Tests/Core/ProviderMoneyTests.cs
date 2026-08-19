using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;

namespace QuotaLens.Tests.Core;

[TestClass]
public sealed class ProviderMoneyTests
{
    [TestMethod]
    public void For_WithMatchedSubscription_UsesPlanValue()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "Claude Code · Max",
            Primary = new RateWindow { Label = "5h Pool", UsedPercent = 50, WindowMinutes = 5 * 60 },
        };
        var score = ProviderPriority.Score("claude", snapshot);

        var money = ProviderMoney.For("claude", snapshot, score);

        Assert.AreEqual(100, money.AmountUsd, 0.001);
        Assert.AreEqual(ProviderMoneyKind.Subscription, money.Kind);
    }

    [TestMethod]
    public void For_WithUnrecognizedPaidPlan_UsesSharedEstimateNotPercent()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "Cursor · Hypernova",
            Primary = new RateWindow { Label = "Monthly credits", UsedPercent = 0, WindowMinutes = 30 * 24 * 60 },
        };
        var score = ProviderPriority.Score("cursor", snapshot);

        var money = ProviderMoney.For("cursor", snapshot, score);

        Assert.AreEqual(20, money.AmountUsd, 0.001);
        Assert.AreEqual(ProviderMoneyKind.Estimate, money.Kind);
    }

    [TestMethod]
    public void For_WithPayAsYouGoBalance_ConvertsToUsd()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "DeepSeek",
            Balance = new BalanceInfo { Total = 23.9, Currency = "CNY" },
        };
        var score = ProviderPriority.Score("deepseek", snapshot);

        var money = ProviderMoney.For("deepseek", snapshot, score);

        Assert.AreEqual(23.9 / 7.2, money.AmountUsd, 0.01);
        Assert.AreEqual(ProviderMoneyKind.Balance, money.Kind);
    }

    [TestMethod]
    public void EstimateMonthlyUsd_WithPooledUnrecognizedAccounts_ScalesSmallestPaidTier()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "codex-lb",
            Accounts =
            {
                new AccountInfo { Plan = "hypernova" },
                new AccountInfo { Plan = "hypernova" },
            },
        };

        var estimate = PlanValueRules.EstimateMonthlyUsd("codex-lb", snapshot);

        Assert.AreEqual(16, estimate, 0.001);
    }
}
