<div align="center">

# QuotaLens

**一眼就知道现在该用哪个 AI 编程工具。**

一款原生 Windows 面板，统一查看 **48 家 AI 编程服务商**的额度、用量与余额。<br>
QuotaLens 无后端、无需注册账号，也没有遥测。

[![Build](https://img.shields.io/github/actions/workflow/status/SpookySandwich/QuotaLens/windows-installer.yml?branch=main&style=flat-square&logo=githubactions&logoColor=white&label=build)](https://github.com/SpookySandwich/QuotaLens/actions/workflows/windows-installer.yml)
[![Release](https://img.shields.io/github/v/release/SpookySandwich/QuotaLens?style=flat-square&logo=github&label=release)](https://github.com/SpookySandwich/QuotaLens/releases/latest)
![Platform](https://img.shields.io/badge/平台-Windows%2011%20x64-0078D4?style=flat-square&logo=windows11&logoColor=white)
![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet&logoColor=white)
![WinUI 3](https://img.shields.io/badge/UI-WinUI%203-0078D4?style=flat-square&logo=windows&logoColor=white)
![Providers](https://img.shields.io/badge/服务商-支持%2048%20家-10B981?style=flat-square)
![Privacy](https://img.shields.io/badge/隐私-100%25%20本地%20%7C%20零遥测-success?style=flat-square)
[![License](https://img.shields.io/github/license/SpookySandwich/QuotaLens?style=flat-square&label=license)](LICENSE)

[English](README.md) · **简体中文**

</div>

![QuotaLens 面板：推荐卡片、用量时间线与服务商卡片](docs/images/dashboard.png)

你打开 Claude Code，敲下一段 prompt，然后才发现周额度一小时前就见底了。于是换 Codex，也没了。折腾三个工具之后你才终于开始干活。QuotaLens 会盯住你付费订阅的每一个 AI 编程套餐，并在你坐下来时回答唯一重要的问题：**现在哪个还有余量？**

|  |  |  |
| :-- | :-- | :-- |
| **现在用这个**<br>一眼看出哪个付费套餐仍有可用余量。 | **看清全部余量**<br>不用逐个打开工具，也能比较剩余容量。 | **知道何时恢复**<br>重置倒计时让你更容易安排工作。 |
| **一个清爽面板**<br>额度周期、余额和账号集中在一处。 | **48 家服务商**<br>把混用的 AI 编程工具放进同一个视图。 | **快速开始**<br>添加常用工具，剩下的交给 QuotaLens。 |
| **隐私开关**<br>一键遮蔽邮箱和余额，方便共享屏幕。 | **随你排序**<br>优先显示你最关心的套餐与重置时间。 | **原生 Win11 体验**<br>快速、专注，支持英文和简体中文。 |

## 安装

下载最新 **[release](https://github.com/SpookySandwich/QuotaLens/releases/latest)**：

| 文件 | 说明 |
| :-- | :-- |
| `QuotaLens-Setup-<version>-win-x64.exe` | 用户级安装程序，无需管理员权限。创建开始菜单项，桌面快捷方式可选。 |
| `QuotaLens-portable-<version>-win-x64.zip` | 解压后运行 `QuotaLens.exe`。 |

需要 **Windows 11 x64**。构建产物为自包含格式 —— 无需另行安装 .NET 或 Windows App SDK 运行时。

然后启动 QuotaLens，添加你常用的服务商；每次需要选择工具时，看最上面的卡片即可。

## 为什么用 QuotaLens

AI 编程额度散落在不同的应用、CLI、网站、账号和重置周期里。QuotaLens 把它们集中起来，让你不再靠猜、不再因为额度耗尽打断工作，也能更充分地利用已经付费的套餐。

推荐卡片告诉你现在适合使用哪个套餐，时间线展示整体续航，服务商卡片则呈现这项选择背后的额度与重置时间。

<details>
<summary><b>全部 48 家服务商</b></summary>

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

## 隐私

QuotaLens 在本机运行，无需注册 QuotaLens 账号、没有 QuotaLens 后端，也不会发送遥测；它只访问你主动配置的服务商。

## 文档

- **[使用指南](docs/user-guide.zh-CN.md)** —— 配置、平台连接、面板说明、故障排查与本地数据。
- **[开发指南](docs/developer-guide.md)** —— 架构、构建、测试与打包约定。

## 致谢

Peter Steinberger 的 **[CodexBar](https://github.com/steipete/CodexBar)** —— 这款 macOS 菜单栏额度追踪工具的服务商实现和细致入微的逐家笔记，是 QuotaLens 用量 API 知识的主要来源。

Soju06 的 **[codex-lb](https://github.com/Soju06/codex-lb)** —— codex-lb 服务商所读取的共享账号负载均衡器。

两者均采用 MIT 许可证；详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 许可证

[MIT](LICENSE)
