# QuotaLens user guide

## Install and start

Download either the per-user installer or portable archive from the latest [QuotaLens release](https://github.com/SpookySandwich/QuotaLens/releases/latest). QuotaLens requires Windows 11 x64; the release is self-contained and does not require a separate .NET or Windows App SDK installation.

After launch:

1. Select **Add Provider**.
2. Choose a provider and complete the connection shown in its settings.
3. Wait for QuotaLens to verify live quota data, then select **Done**.
4. Use the recommendation at the top of the dashboard to choose a tool with useful remaining capacity.

## Read the dashboard

- **Recommended** favors the highest-value active plan that still has headroom. Pay-as-you-go balances are treated as fallback capacity.
- **Usage timeline** compares estimated weekly tokens remaining across configured plans. Segment width represents remaining capacity rather than percentage used.
- **Provider cards** show quota windows, reset countdowns, balances, account breakdowns, and warning or recovery actions.
- **Privacy mode** masks emails and balances when sharing the screen.
- **Sort** can prioritize plan value, reset frequency, or the next reset.
- **Launch** opens the tool represented by the selected source when its executable or URL can be resolved.

The card heading includes the active plan when the provider exposes one. Missing, inactive, or expired plan identity is omitted rather than guessed.

## Add or edit a provider

Every provider uses the same settings dialog. Only fields and actions supported by that provider are shown.

When several data sources are available, they use three common choices:

- **App** reads an installed desktop application's session or local quota service.
- **CLI** reads the provider CLI's existing login and may ask that CLI to renew its own token when this is safe.
- **Web** uses a saved browser session or a remote/API credential entered in settings.

An explicitly selected source is strict: QuotaLens reports that source's problem instead of silently reading a different account. If no source is selected, QuotaLens may use the first ready source.

Executable fields are usually optional overrides. Leave one empty to use the displayed default and automatic detection. If you enter a path, it must exist. The connection helper below the path uses the current draft path, so changing the executable also changes what **Sign in** or **Open app** launches.

Some connections must return real data before **Done** is enabled. While an app or sign-in flow is starting, the settings dialog shows progress and polls the selected source. Selecting **Done** performs one final live fetch; a failure keeps the dialog open and shows the error.

## Provider notes

### Gemini

Gemini **App** supports both Antigravity and Antigravity IDE because they expose the same local quota service. Leave the app path empty to detect either installation. The local service must be running before App data becomes ready.

If **Automatically start Antigravity in background** is enabled, QuotaLens starts the selected app before refresh when the service is unavailable and waits for data. Gemini **CLI** remains a separate source and its Launch action opens the configured CLI in a terminal.

### Kimi

Kimi **App** reuses the desktop application's short-lived session. Kimi renews that session only while the app is in use; QuotaLens does not rewrite or rotate Kimi-owned credentials.

If an explicitly selected App source becomes stale, use **Open app** and wait for Kimi to renew its session. QuotaLens watches the session file and refreshes quota and plan information automatically afterward. With automatic source selection, another ready Kimi source may be used while App is unavailable.

### Grok

Grok reuses the existing Grok CLI login. If the card requests authentication, run `grok login` using the configured executable and refresh the provider afterward.

## Troubleshooting

### Done is disabled

- Complete required fields and correct any path marked **File not found**.
- If the source requires verification, use its **Sign in** or **Open app** helper and wait for live data.
- Changing a verified executable path requires verification again.

### A card has no plan name

QuotaLens displays a plan only when the selected source returns active plan identity. Refresh the provider after renewing its app, CLI, or browser session. A provider that exposes quota without subscription metadata may still show valid usage without a plan suffix.

### Launch is missing

The Launch action appears only when the selected source has a resolvable executable or URL. Check the source selection and its path. Leaving an optional path empty uses automatic detection; a typed path must point to an existing file.

### App data is stale or unavailable

Open the configured app and refresh. Some local services exist only while their app is running, and some apps renew credentials only after they become active.

## Privacy and local data

QuotaLens has no QuotaLens backend, account, or telemetry. It contacts only the providers you configure.

Configuration and recent healthy snapshots are stored under `%LOCALAPPDATA%\QuotaLens`. API keys entered in settings are stored as plain JSON protected by the Windows user account's file permissions; they are not additionally encrypted. Embedded browser sessions use local WebView2 profiles. Provider-owned CLI and app credential stores are read but not rewritten by QuotaLens.

If plain local storage is unsuitable for a particular API key, use a provider source that can reuse an existing app, CLI, or browser login instead.
