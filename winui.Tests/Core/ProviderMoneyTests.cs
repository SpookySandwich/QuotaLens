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
    public void For_WithEuroPayAsYouGoBalance_ConvertsToUsd()
    {
        var snapshot = new ProviderSnapshot
        {
            Name = "Mistral",
            Balance = new BalanceInfo { Total = 10, Currency = "EUR" },
        };
        var score = ProviderPriority.Score("mistral", snapshot);

        var money = ProviderMoney.For("mistral", snapshot, score);

        Assert.AreEqual(10 * CurrencyRates.UsdPerEur, money.AmountUsd, 0.01);
        Assert.AreEqual(ProviderMoneyKind.Balance, money.Kind);
    }

    [TestMethod]
    public void For_WithNonMonetaryBalance_IsWorthNothingInDollars()
    {
        // Venice bills in DIEM. There is no rate that turns it into money, so the
        // value chart must not size a dollar bar from a token count.
        var snapshot = new ProviderSnapshot
        {
            Name = "Venice",
            Balance = new BalanceInfo { Total = 500, Currency = "DIEM" },
        };
        var score = ProviderPriority.Score("venice", snapshot);

        var money = ProviderMoney.For("venice", snapshot, score);

        Assert.IsTrue(score.HasBalance, "the card still shows the DIEM balance");
        Assert.AreEqual(0, money.AmountUsd, 0.001);
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
