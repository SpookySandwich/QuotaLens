using Microsoft.VisualStudio.TestTools.UnitTesting;
using QuotaLens.Core;
using QuotaLens.Services;
using QuotaLens.ViewModels;

namespace QuotaLens.Tests.ViewModels;

[TestClass]
public sealed class ProviderItemViewModelTests
{
    [TestMethod]
    public void IsShimmerLoadingActive_WhenProviderIsRefreshing_ReturnsTrue()
    {
        // Arrange
        var service = new FakeProviderService();
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = service.GetSnapshot(service.Instances[0].Id);

        // Act
        viewModel.Update(snapshot, refreshing: true);

        // Assert
        Assert.IsTrue(viewModel.IsShimmerLoadingActive);
    }

    [TestMethod]
    public void IsShimmerLoadingActive_WhenProviderIsNotRefreshing_ReturnsFalse()
    {
        // Arrange
        var service = new FakeProviderService();
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = service.GetSnapshot(service.Instances[0].Id);

        // Act
        viewModel.Update(snapshot, refreshing: false);

        // Assert
        Assert.IsFalse(viewModel.IsShimmerLoadingActive);
    }

    [TestMethod]
    public void Constructor_ForLaunchableProvider_ExposesLaunchButtonMetadata()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("qoder", "qoder", "Qoder"));
        service.Config.Set("qoder_app_path", executable);

        try
        {
            File.WriteAllText(executable, "");
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

            Assert.IsTrue(viewModel.CanLaunch);
            Assert.AreEqual("Qoder", viewModel.LaunchTargetName);
            Assert.AreEqual("Launch Qoder", viewModel.LaunchAutomationName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Constructor_GeminiAutomaticCliFallbackKeepsDefaultAntigravityLaunch()
    {
        var executable = Path.Combine(
            Path.GetTempPath(),
            $"QuotaLensTest-{Guid.NewGuid():N}-Antigravity.exe");
        var service = new FakeProviderService(
            new ProviderInstance("gemini-work", "gemini", "Gemini"))
        {
            SnapshotOverride = new ProviderSnapshot
            {
                ProviderId = "gemini-work",
                Name = "Gemini",
                SourceState = new ProviderSourceState(null, "cli", UsedFallback: true),
                Primary = new RateWindow { Label = "Daily", UsedPercent = 20 },
            },
        };
        service.Config.Set("gemini_app_path", executable);

        try
        {
            File.WriteAllBytes(executable, []);
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

            Assert.IsTrue(viewModel.CanLaunch);
            Assert.AreEqual("Antigravity", viewModel.LaunchTargetName);
            Assert.AreEqual("Launch Antigravity", viewModel.LaunchAutomationName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Update_WhenInvalidSnapshotCarriesLaunchRecovery_ShowsRenewAction()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("kimi", "kimi", "Kimi"));
        service.Config.Set("kimi_app_path", executable);

        try
        {
            File.WriteAllText(executable, "");
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
            viewModel.Update(new ProviderSnapshot
            {
                ProviderId = "kimi",
                Name = "Kimi",
                Error = "Not available",
                RecoveryAction = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "kimi.appSourceNote"),
            }, refreshing: false);

            Assert.IsTrue(viewModel.CanRenewSession);
            StringAssert.Contains(viewModel.RenewSessionText, "Kimi");
            StringAssert.Contains(viewModel.RenewSessionToolTip, "Kimi");
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Update_WhenHealthyFallbackDataExists_HidesRenewAction()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("kimi", "kimi", "Kimi"));
        service.Config.Set("kimi_app_path", executable);

        try
        {
            File.WriteAllText(executable, "");
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
            viewModel.Update(new ProviderSnapshot
            {
                ProviderId = "kimi",
                Name = "Kimi",
                Primary = new RateWindow { Label = "Weekly", UsedPercent = 20 },
                SourceState = new ProviderSourceState("app", "cli", UsedFallback: true),
                SourceLabel = "Kimi Code CLI",
            }, refreshing: false);

            Assert.IsFalse(viewModel.CanRenewSession);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Update_WhenAppCannotLaunch_HidesRenewAction()
    {
        var service = new FakeProviderService(new ProviderInstance("kimi", "kimi", "Kimi"));
        service.Config.Set("kimi_app_path", @"C:missingKimi.exe");

        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        viewModel.Update(new ProviderSnapshot
        {
            ProviderId = "kimi",
            Name = "Kimi",
            Error = "Not available",
            RecoveryAction = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "kimi.appSourceNote"),
        }, refreshing: false);

        Assert.IsFalse(viewModel.CanRenewSession);
    }

    [TestMethod]
    public void Update_WhenHealthySnapshotIncorrectlyCarriesRecovery_HidesRenewAction()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("kimi", "kimi", "Kimi"));
        service.Config.Set("kimi_app_path", executable);

        try
        {
            File.WriteAllText(executable, "");
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
            viewModel.Update(new ProviderSnapshot
            {
                ProviderId = "kimi",
                Name = "Kimi",
                Primary = new RateWindow { Label = "Weekly", UsedPercent = 20 },
                RecoveryAction = new ProviderRecoveryAction(ProviderRecoveryKind.LaunchApp, "kimi.appSourceNote"),
            }, refreshing: false);

            Assert.IsFalse(viewModel.CanRenewSession);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Constructor_WhenLaunchExecutableIsMissing_HidesLaunchButton()
    {
        var service = new FakeProviderService(new ProviderInstance("qoder", "qoder", "Qoder"));
        service.Config.Set("qoder_app_path", @"C:missingQoderWork.exe");

        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

        Assert.IsFalse(viewModel.CanLaunch);
    }

    [TestMethod]
    public void Constructor_UsesInstanceTypeAndNameInsteadOfInferringFromId()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("work", "qoder", "Work Qoder"));
        service.Config.Set("qoder_app_path", executable);

        try
        {
            File.WriteAllText(executable, "");
            var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

            Assert.AreEqual("work", viewModel.InstanceId);
            Assert.AreEqual("qoder", viewModel.ProviderType);
            Assert.AreEqual("Work Qoder", viewModel.DefaultName);
            Assert.IsTrue(viewModel.CanLaunch);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Constructor_ForProviderWithoutDefaultEditor_HidesLaunchButton()
    {
        var service = new FakeProviderService(new ProviderInstance("deepseek", "deepseek", "DeepSeek"));

        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

        Assert.IsFalse(viewModel.CanLaunch);
    }

    [TestMethod]
    public void DeleteCommand_WhenExecuted_RaisesDeleteRequested()
    {
        var service = new FakeProviderService(new ProviderInstance("deepseek", "deepseek", "DeepSeek"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        ProviderItemViewModel? requested = null;
        viewModel.DeleteRequested += (_, item) => requested = item;

        viewModel.DeleteCommand.Execute(null);

        Assert.AreSame(viewModel, requested);
    }

    [TestMethod]
    public void RefreshLaunchAvailability_WithDefaultEditor_ShowsLaunchButtonForOtherProviders()
    {
        var executable = TempExecutablePath();
        var service = new FakeProviderService(new ProviderInstance("deepseek", "deepseek", "DeepSeek"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

        try
        {
            File.WriteAllText(executable, "");
            service.Config.Set(Catalog.DefaultLaunchEditorPathKey, executable);
            viewModel.RefreshLaunchAvailability();

            Assert.IsTrue(viewModel.CanLaunch);
            Assert.AreEqual("Default editor", viewModel.LaunchTargetName);
            Assert.AreEqual("Launch Default editor", viewModel.LaunchAutomationName);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [TestMethod]
    public void Update_WithLoginRequiredError_DoesNotOfferCardSignIn()
    {
        var service = new FakeProviderService(new ProviderInstance("kimi", "kimi", "Kimi"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = ProviderSnapshot.ForError("kimi", "Kimi", "Kimi WebView", "Login required - click to open Kimi in browser");

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(CardKind.Error, viewModel.Kind);
        StringAssert.Contains(viewModel.ErrorText, "Login required");
    }

    [TestMethod]
    public void LaunchCommand_DelegatesToTheSourceAwareProviderService()
    {
        var service = new FakeProviderService(new ProviderInstance("gemini-work", "gemini", "Gemini"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);

        viewModel.LaunchCommand.Execute(null);

        CollectionAssert.AreEqual(new[] { "gemini-work" }, service.LaunchedIds.ToArray());
    }

    [TestMethod]
    public void Update_WithExpiredEntitlement_UsesProviderNameWithoutStalePlanOrStatus()
    {
        // Arrange
        var service = new FakeProviderService(new ProviderInstance("mimo", "mimo", "MiMo"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = "MiMo · Standard · Plan expired",
            EntitlementStatus = EntitlementStatus.Expired,
            Primary = new RateWindow { Label = "Plan expired", UsedPercent = 100 },
        };

        // Act
        viewModel.Update(snapshot, refreshing: false);

        // Assert
        Assert.AreEqual("MiMo", viewModel.Name);
    }

    [TestMethod]
    public void Update_WithActiveEntitlement_KeepsActivePlanInProviderTitle()
    {
        // Arrange
        var service = new FakeProviderService(new ProviderInstance("mimo", "mimo", "MiMo"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = "MiMo · Standard",
            EntitlementStatus = EntitlementStatus.Active,
            Primary = new RateWindow { Label = "Standard", UsedPercent = 10 },
        };

        // Act
        viewModel.Update(snapshot, refreshing: false);

        // Assert
        Assert.AreEqual("MiMo · Standard", viewModel.Name);
    }

    [TestMethod]
    public void AccountRowViewModel_WithWindowBreakdown_ExposesBothQuotaWindows()
    {
        var account = new AccountInfo
        {
            Email = "user@example.com",
            UsedPercent = 49,
            PrimaryLabel = "5h",
            PrimaryUsedPercent = 10,
            PrimaryResetsAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O"),
            SecondaryLabel = "Weekly",
            SecondaryUsedPercent = 40,
            SecondaryResetsAt = DateTimeOffset.UtcNow.AddDays(2).ToString("O"),
        };

        var row = new AccountRowViewModel(account, 0);

        Assert.AreEqual("user@example.com", row.Name);
        Assert.IsTrue(row.HasWindowBreakdown);
        Assert.IsFalse(row.HasSinglePercent);
        Assert.AreEqual("5h", row.PrimaryLabel);
        Assert.AreEqual("90%", row.PrimaryAvailableText);
        Assert.AreEqual("Weekly", row.SecondaryLabel);
        Assert.AreEqual("60%", row.SecondaryAvailableText);
        Assert.IsTrue(row.HasPrimaryResetText);
        Assert.IsTrue(row.HasSecondaryResetText);
        Assert.IsFalse(row.PrimaryResetText!.Contains("resets in", StringComparison.Ordinal));
        Assert.IsFalse(row.SecondaryResetText!.Contains("resets in", StringComparison.Ordinal));
        StringAssert.StartsWith(row.PrimaryResetToolTip, "5h resets in");
        StringAssert.StartsWith(row.SecondaryResetToolTip, "Weekly resets in");
    }

    [TestMethod]
    public void AccountRowViewModel_WithWeeklyPrimary_PreservesFullLabelAndPercent()
    {
        // Arrange
        var account = new AccountInfo
        {
            Email = "user@example.com",
            PrimaryLabel = "Weekly",
            PrimaryUsedPercent = 35,
        };

        // Act
        var row = new AccountRowViewModel(account, 0);

        // Assert
        Assert.AreEqual("Weekly", row.PrimaryLabel);
        Assert.AreEqual("65%", row.PrimaryAvailableText);
    }

    [TestMethod]
    public void AccountRowViewModel_WhenSensitiveInfoHidden_UsesAccountNumber()
    {
        var account = new AccountInfo
        {
            Email = "user@example.com",
            PrimaryUsedPercent = 10,
        };

        var row = new AccountRowViewModel(account, 1, hideSensitive: true);

        Assert.AreEqual("Account 2", row.Name);
        Assert.IsTrue(row.IsNameHidden);
        Assert.IsTrue(row.PrivacyPlaceholderWidth > 0);
    }

    [TestMethod]
    public void Update_WithAdditionalWindows_RendersRowsAfterStandardWindows()
    {
        var service = new FakeProviderService(new ProviderInstance("codex", "codex", "Codex"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = service.GetSnapshot("codex")!;
        snapshot.Secondary = new RateWindow { Label = "Weekly Pool", UsedPercent = 20 };
        snapshot.Tertiary = new RateWindow { Label = "Credits", UsedPercent = 0 };
        snapshot.AdditionalWindows.Add(new RateWindow { Label = "Codex Spark 5-hour", UsedPercent = 30 });
        snapshot.AdditionalWindows.Add(new RateWindow { Label = "Codex Spark Weekly", UsedPercent = 40 });

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(5, viewModel.Rows.Count);
        Assert.AreEqual("5h Pool", viewModel.Rows[0].Label);
        Assert.AreEqual("Weekly Pool", viewModel.Rows[1].Label);
        Assert.AreEqual("Credits", viewModel.Rows[2].Label);
        Assert.AreEqual("Codex Spark 5-hour", viewModel.Rows[3].Label);
        Assert.AreEqual("Codex Spark Weekly", viewModel.Rows[4].Label);
    }

    [TestMethod]
    public void Update_WithInformationalMetric_RendersValueWithoutQuotaSemantics()
    {
        var service = new FakeProviderService(new ProviderInstance("openai", "openai", "OpenAI API"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "openai",
            Name = "OpenAI API",
            Primary = new RateWindow
            {
                Label = "30-day cost",
                Kind = RateWindowKind.Informational,
                UsedPercent = 100,
                ValueText = "$12.34",
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(CardKind.Rate, viewModel.Kind);
        Assert.AreEqual(1, viewModel.Rows.Count);
        Assert.IsFalse(viewModel.Rows[0].IsQuota);
        Assert.IsTrue(viewModel.Rows[0].IsInformational);
        Assert.AreEqual("$12.34", viewModel.Rows[0].ValueText);
        Assert.AreEqual("30-day cost: $12.34", viewModel.Rows[0].AutomationName);
        Assert.AreEqual(100, viewModel.AvailablePercent);
    }

    [TestMethod]
    public void QuotaRow_WithoutResetTime_SurfacesProviderQuotaDescription()
    {
        var row = new QuotaRowViewModel(new RateWindow
        {
            Label = "Monthly limit",
            UsedPercent = 25,
            DetailText = "$75 of $100 remaining · resets monthly",
        });

        Assert.IsTrue(row.IsQuota);
        Assert.AreEqual("$75 of $100 remaining · resets monthly", row.ResetText);
    }

    [TestMethod]
    public void QuotaRow_WithResetTime_UsesCanonicalResetInsteadOfProviderProse()
    {
        var row = new QuotaRowViewModel(new RateWindow
        {
            Label = "Weekly limit",
            UsedPercent = 25,
            ResetsAt = DateTimeOffset.UtcNow.AddHours(3).AddMinutes(13).ToString("O"),
            DetailText = "You have used some of your weekly limit, it will fully refresh later.",
        });

        StringAssert.StartsWith(row.ResetText, "resets in 3h 12m");
    }

    [TestMethod]
    public void Update_WithAlibabaPlanAndBalance_RendersRateRowsWithInlineBalance()
    {
        var service = new FakeProviderService(new ProviderInstance("alibaba", "alibaba", "Alibaba"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "alibaba",
            Name = "Alibaba · Coding Plan Pro",
            Primary = new RateWindow
            {
                Label = "5h Pool",
                UsedPercent = 10,
                WindowMinutes = 5 * 60,
            },
            Secondary = new RateWindow
            {
                Label = "Weekly",
                UsedPercent = 25,
                WindowMinutes = 7 * 24 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "CNY",
                Total = 88.5,
                Paid = 80.25,
                Granted = 8.25,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(CardKind.Rate, viewModel.Kind);
        Assert.AreEqual(2, viewModel.Rows.Count);
        Assert.AreEqual("5h Pool", viewModel.Rows[0].Label);
        Assert.AreEqual("Weekly", viewModel.Rows[1].Label);
        Assert.AreEqual("¥88.50 balance", viewModel.InlineBalance);
    }

    [TestMethod]
    public void Update_WithPayAsYouGoBalanceAndUsage_RendersRateRowsWithInlineBalance()
    {
        var service = new FakeProviderService(new ProviderInstance("deepseek", "deepseek", "DeepSeek"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "deepseek",
            Name = "DeepSeek",
            Primary = new RateWindow
            {
                Label = "Account Balance",
                UsedPercent = 0,
            },
            Secondary = new RateWindow
            {
                Label = "Requests",
                UsedPercent = 20,
                WindowMinutes = 24 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "CNY",
                Total = 12.34,
                Paid = 12.34,
                Granted = 0,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(CardKind.Rate, viewModel.Kind);
        Assert.AreEqual(2, viewModel.Rows.Count);
        Assert.AreEqual("Account Balance", viewModel.Rows[0].Label);
        Assert.AreEqual("Requests", viewModel.Rows[1].Label);
        Assert.AreEqual("¥12.34 balance", viewModel.InlineBalance);
    }

    [TestMethod]
    public void Update_WithUsdBalanceAndRateWindows_RendersInlineBalance()
    {
        var service = new FakeProviderService(new ProviderInstance("claude", "claude", "Claude Code"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "claude",
            Name = "Claude Code · Max",
            Primary = new RateWindow
            {
                Label = "5h Pool",
                UsedPercent = 20,
                WindowMinutes = 5 * 60,
            },
            Secondary = new RateWindow
            {
                Label = "7d Pool",
                UsedPercent = 30,
                WindowMinutes = 7 * 24 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = 8.35,
                Paid = 1.65,
                Granted = 10,
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(CardKind.Rate, viewModel.Kind);
        Assert.AreEqual("$8.35 balance", viewModel.InlineBalance);
        Assert.AreEqual("of $10.00 total", viewModel.InlineBalanceDetail);
    }

    [TestMethod]
    public void Update_WithInformationalBalance_RendersComponentsWithoutRepeatingTotal()
    {
        var service = new FakeProviderService(new ProviderInstance("mimo", "mimo", "MiMo"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = "MiMo",
            Primary = new RateWindow
            {
                Label = "Balance",
                Kind = RateWindowKind.Informational,
                ValueText = "USD 25.51 remaining",
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = 25.51,
                Paid = 20,
                Granted = 5.51,
                PaidLabelKey = "card.cashBalance",
                GrantedLabelKey = "card.giftBalance",
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual(3, viewModel.Rows.Count);
        Assert.AreEqual("Balance", viewModel.Rows[0].Label);
        Assert.AreEqual("Cash", viewModel.Rows[1].Label);
        Assert.AreEqual("$20.00", viewModel.Rows[1].ValueText);
        Assert.AreEqual("Gift", viewModel.Rows[2].Label);
        Assert.AreEqual("$5.51", viewModel.Rows[2].ValueText);
        Assert.IsNull(viewModel.InlineBalance);
        Assert.IsNull(viewModel.InlineBalanceDetail);
    }

    [TestMethod]
    public void Update_WithQuotaAndNamedBalanceComponents_RendersComponentDetailInline()
    {
        var service = new FakeProviderService(new ProviderInstance("mimo", "mimo", "MiMo"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "mimo",
            Name = "MiMo · Standard",
            Primary = new RateWindow
            {
                Label = "Standard",
                UsedPercent = 20,
                WindowMinutes = 30 * 24 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = 25.51,
                Paid = 20,
                Granted = 5.51,
                PaidLabelKey = "card.cashBalance",
                GrantedLabelKey = "card.giftBalance",
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);

        Assert.AreEqual("$25.51 balance", viewModel.InlineBalance);
        Assert.AreEqual("Cash $20.00 · Gift $5.51", viewModel.InlineBalanceDetail);
    }

    [TestMethod]
    public void SetSensitiveHidden_WithAccountsAndBalance_MasksSensitiveDisplayText()
    {
        var service = new FakeProviderService(new ProviderInstance("codex-lb", "codex-lb", "codex-lb"));
        var viewModel = new ProviderItemViewModel(service, service.Instances[0]);
        var snapshot = new ProviderSnapshot
        {
            ProviderId = "codex-lb",
            Name = "codex-lb · owner@example.com",
            Primary = new RateWindow
            {
                Label = "Effective Usage",
                UsedPercent = 20,
                WindowMinutes = 5 * 60,
            },
            Balance = new BalanceInfo
            {
                Currency = "USD",
                Total = 8.35,
                Paid = 1.65,
                Granted = 10,
            },
            Accounts =
            {
                new AccountInfo { Email = "first@example.com", PrimaryUsedPercent = 10 },
                new AccountInfo { Email = "second@example.com", PrimaryUsedPercent = 20 },
            },
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        viewModel.Update(snapshot, refreshing: false);
        viewModel.SetSensitiveHidden(true);

        Assert.AreEqual("codex-lb", viewModel.Name);
        Assert.IsTrue(viewModel.HasNamePrivacyPlaceholder);
        Assert.AreEqual("Balance hidden", viewModel.InlineBalance);
        Assert.IsNull(viewModel.InlineBalanceDetail);
        Assert.AreEqual("Account 1", viewModel.Accounts[0].Name);
        Assert.AreEqual("Account 2", viewModel.Accounts[1].Name);
    }

    private static string TempExecutablePath() =>
        Path.Combine(Path.GetTempPath(), $"QuotaLensTest-{Guid.NewGuid():N}.exe");

    private sealed class FakeProviderService : IProviderService
    {
        private readonly FakeConfig _config = new();
        public List<string> LaunchedIds { get; } = new();
        public ProviderSnapshot? SnapshotOverride { get; set; }

        public FakeProviderService()
            : this(new ProviderInstance("claude", "claude", "Claude Code"))
        {
        }

        public FakeProviderService(ProviderInstance instance)
        {
            Instances = new[] { instance };
        }

        public IConfigService Config => _config;

        public IReadOnlyList<ProviderInstance> Instances { get; }

        public event EventHandler<ProviderSnapshot>? SnapshotUpdated
        {
            add { }
            remove { }
        }

        public event EventHandler<string>? RefreshingChanged
        {
            add { }
            remove { }
        }

        public event EventHandler? InstancesChanged
        {
            add { }
            remove { }
        }

        public event EventHandler<(string Id, int SecondsLeft, int Attempt)>? RateLimited
        {
            add { }
            remove { }
        }

        public ProviderSnapshot? GetSnapshot(string instanceId) =>
            SnapshotOverride ?? new ProviderSnapshot
            {
                ProviderId = instanceId,
                Name = "Claude Code",
                Primary = new RateWindow
                {
                    Label = "5h Pool",
                    UsedPercent = 20,
                },
                UpdatedAt = DateTimeOffset.UtcNow,
            };

        public bool IsRefreshing(string instanceId) => false;

        public Task RefreshAllAsync() => Task.CompletedTask;

        public Task RefreshAsync(string instanceId) => Task.CompletedTask;

        public ProviderInstance AddInstance(string providerType, bool refreshImmediately = true)
        {
            return new ProviderInstance(providerType, providerType, providerType);
        }

        public void RemoveInstance(string instanceId)
        {
        }

        public ProviderLaunchInfo? GetLaunchInfo(string instanceId)
        {
            var instance = Instances.FirstOrDefault(candidate => candidate.Id == instanceId);
            if (instance is null)
                return null;

            var provider = ProviderRegistry.Create(instance.Type);
            return ProviderRegistry.LaunchActionFor(
                    provider,
                    instance.Id,
                    Config)
                ?.GetInfo(instance.Id, Config);
        }

        public void LaunchProvider(string instanceId)
        {
            LaunchedIds.Add(instanceId);
        }

        private sealed class FakeConfig : IConfigService
        {
            private readonly Dictionary<string, string> _values = new();

            public IReadOnlyDictionary<string, string> All => _values;

            public IReadOnlyList<ProviderInstance> Instances { get; } = Array.Empty<ProviderInstance>();

            public double RefreshMs => 1_800_000;

            public string Get(string key, string fallback = "") =>
                _values.TryGetValue(key, out var value) ? value : fallback;

            public string GetScoped(string instanceId, string key, string fallback = "") =>
                _values.TryGetValue($"{instanceId}.{key}", out var scoped) ? scoped : fallback;

            public bool HasScoped(string instanceId, string key) =>
                _values.ContainsKey($"{instanceId}.{key}");

            public bool GetBool(string key, bool fallback = false) =>
                _values.TryGetValue(key, out var value) ? value == "true" : fallback;

            public void Set(string key, string value)
            {
                _values[key] = value;
            }

            public void SetMany(IReadOnlyDictionary<string, string> values)
            {
            }

            public void Remove(string key)
            {
                _values.Remove(key);
            }

            public Task SaveAsync() => Task.CompletedTask;


            public ProviderInstance AddInstance(string providerType) => new(providerType, providerType, providerType);

            public void RemoveInstance(string id)
            {
            }
        }
    }
}
