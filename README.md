<div align="center">

# QuotaLens

**One glance tells you which AI coding tool to use right now.**

A native Windows dashboard for quota, usage, and balances across **49 AI coding providers**.<br>
No backend, no telemetry, nothing to paste.

[![Build](https://img.shields.io/github/actions/workflow/status/SpookySandwich/QuotaLens/windows-installer.yml?branch=main&label=build)](https://github.com/SpookySandwich/QuotaLens/actions/workflows/windows-installer.yml)
[![Release](https://img.shields.io/github/v/release/SpookySandwich/QuotaLens?display_name=tag&label=release)](https://github.com/SpookySandwich/QuotaLens/releases/latest)
[![License](https://img.shields.io/github/license/SpookySandwich/QuotaLens?label=license)](LICENSE)
![Platform](https://img.shields.io/badge/Windows%2011-x64-0078D4)

**English** · [简体中文](README.zh-CN.md)

</div>

![The QuotaLens dashboard: recommendation card, usage timeline, and provider cards](docs/images/dashboard.png)

You open Claude Code, fire off a prompt, and *then* find out the weekly pool ran dry an hour ago. So you try Codex. Also dry. Three tools later you're finally working. QuotaLens watches every AI coding plan you pay for and answers the only question that matters when you sit down: **which one has headroom right now?**

|  |  |  |
| :-- | :-- | :-- |
| **Use this now**<br>Hero card picks the highest-value plan that still has headroom. | **Usage timeline**<br>One bar, one segment per plan, width = tokens left this week. | **Per-window bars**<br>5-hour pool, weekly pool, monthly — with reset countdowns. |
| **Zero setup**<br>Reads the credentials your CLIs already wrote. No keys to paste. | **Browser login**<br>Sign in once in an embedded window; the session is reused. | **49 providers**<br>Grouped picker, ranked instant search, full keyboard flow. |
| **Privacy toggle**<br>One click masks emails and balances for screen sharing. | **Sort your way**<br>By plan value, reset frequency, or next reset. | **Native Win11**<br>Mica, tray icon, single instance, quiet refresh. EN + 简体中文. |

## Install

Download the latest **[release](https://github.com/SpookySandwich/QuotaLens/releases/latest)**:

| File | Notes |
| :-- | :-- |
| `QuotaLens-Setup-<version>-win-x64.exe` | Per-user installer, no admin needed. Start Menu + optional desktop shortcut. |
| `QuotaLens-portable-<version>-win-x64.zip` | Unzip, run `QuotaLens.exe`. |

Requires **Windows 11 x64**. The build is self-contained — no separate .NET or Windows App SDK runtime to install.

Then: launch it. Providers whose CLIs you've already signed into light up on their own. Click **Add Provider** for the rest. Read the top card — that's the tool to use.

## The dashboard

**Recommended** ranks your plans the way you would: the highest-value subscription you're already paying for that *still has headroom*, so you get your money's worth first. Pay-as-you-go API balances sit at the bottom of that ranking — a last resort, never the default pick.

**The usage timeline** is one cumulative bar across every configured plan:

- Each segment is one provider. Its **width is the estimated tokens you have left this week** — not a percentage, so a 5% sliver of a Max 20x plan is still visibly wider than a full free tier.
- Segments run **slowest-refilling first**. The left end is a weekly pool you should spend carefully; the right end is a 5-hour pool that refills over lunch.
- **Spent capacity is not drawn.** The bar is runway, not history.

Below it, provider cards carry per-window progress bars, reset countdowns, green / amber / red status, credit balances, and per-account breakdowns for pooled setups. Sort the deck by plan value, reset frequency, or next reset.

## Adding providers

<p><b>49 providers, grouped by what setup they need:</b></p>
<ul>
<li><b>Browser login</b> — 20. One sign-in in an embedded window; HttpOnly cookies and responses are captured natively, so the window actually closes when you're done.</li>
<li><b>API key</b> — 16. Paste a key, read a balance.</li>
<li><b>Local app or CLI</b> — 13. Picked up from credential files the tool already wrote (Claude Code, Codex, Gemini, Kimi Code).</li>
</ul>
<p>A <b>Suggested</b> group floats what's likely already on your machine. Search is ranked and instant; the whole flow works from the keyboard.</p>

<details>
<summary><b>All 49 providers</b></summary>

<br>

| | | | |
| :-- | :-- | :-- | :-- |
| Abacus AI | Alibaba | Amp | Antigravity |
| Augment | AWS Bedrock | Azure OpenAI | BayesDL |
| Claude Code | Codebuff | Codex | codex-lb |
| Command Code | Crof | Cursor | DeepSeek |
| Deepgram | Doubao | ElevenLabs | Factory |
| Gemini | GitHub Copilot | Grok | Groq |
| JetBrains AI | Kilo | Kimi | Kiro |
| LLM Proxy | Manus | MiMo | MiniMax |
| Mistral | Moonshot | Ollama | OpenAI API |
| OpenCode | OpenCode Go | OpenRouter | Perplexity |
| Qoder | StepFun | Synthetic | T3 Chat |
| Venice | Vertex AI | Warp | Windsurf |
| z.ai | | | |

</details>

## Privacy

QuotaLens is a local tool. There is no backend, no account, and no telemetry. It reads credentials that already exist on your machine — the login files your CLIs wrote, or logins you complete in the embedded browser — queries each provider's own usage endpoint, and caches the last result under `%LOCALAPPDATA%\QuotaLens`. Nothing leaves your machine except the calls to the providers you configured.

API keys you paste are stored in plain JSON there, protected by your Windows user account's file permissions rather than by additional encryption — the same protection your CLI credential files already have. If that isn't acceptable for a given key, prefer a provider that detects a local CLI login instead.

What the embedded login window captures, and what it deliberately doesn't, is audited in [docs/web-login-capture-audit.md](docs/web-login-capture-audit.md).

## Build from source

Prerequisites: Windows 11 and the .NET SDK.

```powershell
dotnet build .\QuotaLens.slnx -c Debug -p:Platform=x64
dotnet test  .\QuotaLens.slnx -c Debug -p:Platform=x64
```

630 tests cover quota math, provider response parsing, refresh scheduling, and the recommendation logic. Append `-- --ui-smoke` to `dotnet run` for a single isolated window with no tray, refreshes, network calls, or login windows — useful when UI automation shouldn't have its focus stolen.

Package the installer and portable zip locally (needs [Inno Setup 6](https://jrsoftware.org/isinfo.php)):

```powershell
.\scripts\package-windows.ps1 -Configuration Release -Platform x64 -Version 1.0.0
```

CI builds and tests every push (`.github/workflows/windows-installer.yml`). Tagging `v*` (or running the Release workflow) packages `1.0.0` and drafts a GitHub release.

## Reference

| Document | What's in it |
| :-- | :-- |
| [plan-token-allowances.md](docs/plan-token-allowances.md) | Every weekly token estimate, its source, and its confidence |
| [provider-plan-evidence.md](docs/provider-plan-evidence.md) | Public pricing sources behind each plan's value ranking |
| [provider-display-conventions.md](docs/provider-display-conventions.md) | Title and status rules for provider cards |
| [web-login-capture-audit.md](docs/web-login-capture-audit.md) | What the embedded login window captures |

## Acknowledgements

**[CodexBar](https://github.com/steipete/CodexBar)** by Peter Steinberger — the macOS menu-bar quota tracker whose provider implementations and meticulous per-provider notes are the source of much of QuotaLens's usage-API knowledge.

**[codex-lb](https://github.com/Soju06/codex-lb)** by Soju06 — the pooled-account load balancer that the codex-lb provider reads from.

Both MIT-licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE)
