# QuotaLens developer guide

QuotaLens is an unpackaged, self-contained WinUI 3 application targeting `net10.0-windows`. Provider integrations are read-only monitors: they may read provider-owned sessions and call usage endpoints, but must not rewrite another application's credential store.

User-facing setup and troubleshooting belong in [user-guide.md](user-guide.md); keep this document focused on implementation and validation.

## Build, test, and package

```powershell
dotnet build .\QuotaLens.slnx -c Debug -p:Platform=x64
dotnet test  .\winui.Tests\QuotaLens.Tests.csproj -c Debug -p:Platform=x64 --no-build
```

Run an isolated window with no tray, refresh, network traffic, or login popups:

```powershell
dotnet run --project .\winui\QuotaLens.csproj -c Debug -p:Platform=x64 -- --ui-smoke
```

Create the version `1.0.0` installer and portable archive with Inno Setup 6 installed:

```powershell
.\scripts\package-windows.ps1 -Configuration Release -Platform x64 -Version 1.0.0
```

## Provider boundary

A provider converts external data into shared models: `ProviderSnapshot`, `RateWindow`, `ModelQuota`, `AccountInfo`, and `BalanceInfo`. Shared UI, sorting, refresh, recovery, and file-watching code consumes those models and must not branch on provider ids.

- Put reset instants in `RateWindow.ResetsAt` and cadence in `WindowMinutes`. `ResetFormatter` alone produces text such as `resets in 3h 12m`.
- Put non-reset usage, balance, or status prose in `DetailText`.
- Put response-specific parsing and authentication inside the provider.
- Put editable fields, default paths, environment mappings, launch targets, and setup probes in `Catalog`.
- Put compatibility names in declarative aliases; do not scatter migration conditionals through providers or views.

## Provider and plan identity

Provider identity and plan identity are separate data:

- `ProviderSnapshot.Name` leaves a provider parser as the stable provider name, never a presentation title.
- `PlanId` is the provider's canonical machine identity when one exists.
- `PlanName` is the provider's human-readable active plan name.
- `ProviderSnapshotIdentity` is the only component allowed to compose the visible `Provider · Plan` title. It also normalizes missing/expired plans and upgrades titles persisted by older builds.

Do not concatenate a provider name and plan inside a provider, web parser, source, or view model. `ProviderSnapshotMetadata.Apply` normalizes snapshots at the shared metadata boundary, while `ProviderSnapshotIdentity.ComposeTitle` handles instance names at display/persistence boundaries. Pricing and token rules consume `ProviderPlanIdentity` rather than reparsing the title.

`ProviderSnapshotIdentityTests.ProviderParsers_DoNotComposePresentationTitles` scans provider sources and fails if a parser assigns a title containing ` · `. Add parser assertions for bare `Name` plus structured `PlanId`/`PlanName` whenever a provider gains plan support.

Some providers expose quota and plan metadata through different resources. Combining resources is valid when they belong to the selected source and authenticated account; it is enrichment, not a fallback to another data source. Keep valid quota when optional plan enrichment fails.

- Grok reads quota from `GET /billing?format=credits` and subscription identity from `GET /user?include=subscription` on the CLI backend. Current agent-stdio builds return `Method not found` for `x.ai/billing`; ACP remains compatibility fallback only.
- Kimi App reads coding quota from `BillingService/GetUsages` and active goods identity from `MembershipService/GetSubscription`. The membership level becomes `PlanId` and the goods title becomes `PlanName`.

## App, CLI, and Web sources

Every multi-source provider exposes `ProviderSource` entries. `ProviderSourceMode` is deliberately closed to exactly `App`, `Cli`, and `Web`; the stored values are `app`, `cli`, and `web`.

Each source declares:

- availability and fetch delegates;
- the configuration fields visible for that mode;
- optional legacy stored values;
- optional attention and recovery metadata;
- optional session files to watch.

`ProviderSourceRunner` owns all selection behavior. An explicit selection is strict and never falls through to another account. With no selection, the first available source wins. It rejects duplicate modes. `EditProviderDialog` renders the same selector and fields for every provider from this metadata.

App executable fields are optional overrides. Their matching `ProviderLaunchTarget` must contain usable default paths so an empty input displays and uses auto-detection. A provider that supports multiple compatible app variants should list all paths under one App source rather than inventing provider-specific source values.

Gemini is the reference case: its App source reads the common local quota protocol exposed by Antigravity (`language_server.exe`) and Antigravity IDE (`language_server_windows_x64.exe`). One shared path field auto-detects either executable; one of the apps must be running for its local service to exist. Gemini's CLI source remains separate.

Kimi is the short-lived-session reference case. Its desktop token is renewed only by Kimi while that app is in use; QuotaLens never rotates provider-owned credentials. An explicit App selection therefore remains invalid while the token is stale and exposes its launch recovery action. Automatic selection may use another ready source. The App source watches Kimi's token store and refreshes quota plus plan identity after Kimi renews the session.

## Configuration and UI contracts

All addable providers open the same settings dialog. Source selection determines visible fields. App/CLI/Web tabs have stable automation ids, and every input and browse action has an automation id derived from the instance and field key.

An empty optional path is valid and means “use the displayed default.” A typed path must exist. **Done** saves the scoped/global values and performs a live fetch; failure keeps the dialog open with the error.

Recovery actions are structural data on invalid snapshots. Healthy snapshots never show recovery controls. The card only executes a declared action and never probes provider identity or parses error wording.

## Validation expectations

Changes to provider routing require parser, source-order, strict-selection, migration, availability, and error-path tests. Changes to identity require bare-provider parser assertions, structured plan assertions, shared title-composition coverage, and the provider-source architecture guard. Changes to shared configuration UI also require a shadow-copy launch, UI Automation of the affected dialog states, and screenshots at the minimum, representative, and maximized sizes. Keep the working tree's build output separate from the launched copy to avoid locked WinUI files.

The maintained Markdown set is intentionally small: the English and Chinese READMEs, their user guides, this developer guide, and the legally required third-party notices.
