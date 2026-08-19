<div align="center">

# QuotaLens

**One glance tells you which AI coding tool to use right now.**

A native Windows dashboard for quota, usage, and balances across **50 AI coding providers**.<br>
No QuotaLens backend, no account, and no telemetry.

[![Build](https://img.shields.io/github/actions/workflow/status/SpookySandwich/QuotaLens/windows-installer.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=build)](https://github.com/SpookySandwich/QuotaLens/actions/workflows/windows-installer.yml)
[![Release](https://img.shields.io/github/v/release/SpookySandwich/QuotaLens?style=flat-square&logo=github&label=release)](https://github.com/SpookySandwich/QuotaLens/releases/latest)
![Platform](https://img.shields.io/badge/platform-Windows%2011%20x64-0078D4?style=flat-square&logo=windows11&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4?style=flat-square&logo=windows&logoColor=white)
![Providers](https://img.shields.io/badge/providers-50%20supported-10B981?style=flat-square)
![Privacy](https://img.shields.io/badge/privacy-100%25%20local-success?style=flat-square)
[![License](https://img.shields.io/github/license/SpookySandwich/QuotaLens?style=flat-square&label=license)](LICENSE)

**English** · [简体中文](README.zh-CN.md)

</div>

![The QuotaLens dashboard: recommendation card, usage timeline, and provider cards](docs/images/dashboard.png)

You open Claude Code, fire off a prompt, and *then* find out the weekly pool ran dry an hour ago. So you try Codex. Also dry. Three tools later you're finally working. QuotaLens watches every AI coding plan you pay for and answers the only question that matters when you sit down: **which one has headroom right now?**

|  |  |  |
| :-- | :-- | :-- |
| **Use this now**<br>See which paid plan still has useful headroom. | **See all your runway**<br>Compare remaining capacity without opening every tool. | **Know when it returns**<br>Reset countdowns make it easy to plan around limits. |
| **One calm dashboard**<br>Quota windows, balances, and accounts in one place. | **50 providers**<br>Bring a mixed AI coding toolkit into one view. | **Quick setup**<br>Add the tools you use and let QuotaLens keep watch. |
| **Privacy toggle**<br>One click masks emails and balances for screen sharing. | **Sort your way**<br>Surface the plans and resets that matter to you. | **Native Win11**<br>A fast, focused desktop app in English and 简体中文. |

## Install

Download the latest **[release](https://github.com/SpookySandwich/QuotaLens/releases/latest)**:

| File | Notes |
| :-- | :-- |
| `QuotaLens-Setup-<version>-win-x64.exe` | Per-user installer, no admin needed. Start Menu + optional desktop shortcut. |
| `QuotaLens-portable-<version>-win-x64.zip` | Unzip, run `QuotaLens.exe`. |

Requires **Windows 11 x64**. The build is self-contained — no separate .NET or Windows App SDK runtime to install.

Then launch QuotaLens, add the providers you use, and check the top card whenever you need to choose a tool.

## Why QuotaLens

AI coding limits are scattered across apps, CLIs, websites, accounts, and reset windows. QuotaLens brings them together so you can stop guessing, avoid interrupted work, and get more value from the plans you already pay for.

The recommendation points you toward a useful plan now, the timeline shows your remaining runway, and provider cards show the limits and reset times behind that choice.

<details>
<summary><b>All 50 providers</b></summary>

<br>

| | | | |
| :-- | :-- | :-- | :-- |
| Abacus AI | Alibaba | Amp | Augment |
| AWS Bedrock | Azure OpenAI | BayesDL | Claude Code |
| Codebuff | Codex | codex-lb | Command Code |
| Crof | Cursor | DeepSeek | Deepgram |
| Doubao | ElevenLabs | Factory | Gemini |
| GitHub Copilot | Grok | Groq | JetBrains AI |
| Kilo | Kimi | Kiro | LLM Proxy |
| Manus | MiMo | MiniMax | Mistral |
| Moonshot | Ollama | OpenAI API | OpenCode |
| OpenCode Go | OpenRouter | Perplexity | Qoder |
| StepFun | Synthetic | T3 Chat | Venice |
| Vertex AI | Warp | Windsurf | z.ai |

</details>

## Privacy

QuotaLens runs locally, has no QuotaLens account or backend, and sends no telemetry. It contacts only the providers you configure.

## Documentation

- **[User guide](docs/user-guide.md)** — setup, provider connections, dashboard behavior, troubleshooting, and local data.
- **[Developer guide](docs/developer-guide.md)** — architecture, build, test, and packaging conventions.

## Acknowledgements

**[CodexBar](https://github.com/steipete/CodexBar)** by Peter Steinberger — the macOS menu-bar quota tracker whose provider implementations and meticulous per-provider notes are the source of much of QuotaLens's usage-API knowledge.

**[codex-lb](https://github.com/Soju06/codex-lb)** by Soju06 — the pooled-account load balancer that the codex-lb provider reads from.

Both MIT-licensed; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## License

[MIT](LICENSE)
