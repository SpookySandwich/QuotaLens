# QuotaLens Session Handoff — 2026-08-16

## Current architecture

Shared quota behavior is provider-agnostic:

- Providers parse external data into `ProviderSnapshot`, `RateWindow`, `ModelQuota`, and `AccountInfo`.
- `ResetFormatter` owns card reset wording. A valid `ResetsAt` renders as compact text such as `resets in 3h 12m`; provider reset prose and ISO strings do not reach the card.
- `ProviderSourceRunner` owns multi-source selection. Explicit selections are strict; automatic mode uses the first available source.
- `ProviderRecoveryAction` is carried only by invalid snapshots. Healthy data never displays the recovery button.
- `IProviderSource.WatchPaths` and `ProviderSourceFileWatcher` provide generic session-file monitoring.
- Availability, model-family grouping, cadence, and priority derive from structured snapshot fields, not provider IDs.
- Renamed configuration fields are data in `Catalog.ConfigKeyAliases` and migrate generically.

## Kimi

- The App source reads Electron `safeStorage.v1` and legacy token stores through the generic safe-storage reader.
- Windows DPAPI unwraps the Electron master key; Chromium `v10` / `v11` values use AES-GCM.
- The App usage response exposes `totalQuota`, which is shown together with Weekly and the 5h rate limit.
- Kimi owns token rotation and renews the short-lived token during real app activity. QuotaLens does not replay private refresh behavior. It observes token-store changes and refetches automatically; an explicitly selected invalid App source offers `Open Kimi to refresh`.

## Validation

- x64 Debug build: zero warnings and zero errors.
- Automated suite: 727 passed, 0 failed.
- Shadow-copy UI smoke used isolated config/snapshots and covered 520×720, 620×720, and maximized layouts, healthy/error cards, compact reset text, Kimi total quota, recovery-button visibility, Settings navigation, and UI Automation semantics.
- `System.Security.Cryptography.ProtectedData` is pinned to NuGet's latest stable version, `10.0.11`.

The user's installed app, real configuration, sessions, and provider data were not modified.
