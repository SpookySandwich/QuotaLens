<div align="center">

# QuotaLens

**一眼就知道现在该用哪个 AI 编程工具。**

一款原生 Windows 面板，统一查看 **49 家 AI 编程服务商**的额度、用量与余额。<br>
无后端，无遥测，无需粘贴任何东西。

[![Build](https://img.shields.io/github/actions/workflow/status/SpookySandwich/QuotaLens/windows-installer.yml?branch=main&label=build)](https://github.com/SpookySandwich/QuotaLens/actions/workflows/windows-installer.yml)
[![Release](https://img.shields.io/github/v/release/SpookySandwich/QuotaLens?display_name=tag&label=release)](https://github.com/SpookySandwich/QuotaLens/releases/latest)
[![License](https://img.shields.io/github/license/SpookySandwich/QuotaLens?label=license)](LICENSE)
![Platform](https://img.shields.io/badge/Windows%2011-x64-0078D4)

[English](README.md) · **简体中文**

</div>

![QuotaLens 面板：推荐卡片、用量时间线与服务商卡片](docs/images/dashboard.png)

你打开 Claude Code，敲下一段 prompt，然后才发现周额度一小时前就见底了。于是换 Codex，也没了。折腾三个工具之后你才终于开始干活。QuotaLens 会盯住你付费订阅的每一个 AI 编程套餐，并在你坐下来时回答唯一重要的问题：**现在哪个还有余量？**

|  |  |  |
| :-- | :-- | :-- |
| **现在用这个**<br>主卡片挑出仍有余量的最高价值套餐。 | **用量时间线**<br>一条横条，每个套餐一段，宽度 = 本周剩余 token。 | **分周期进度条**<br>5 小时池、每周池、每月池 —— 附带重置倒计时。 |
| **零配置**<br>直接读取 CLI 已经写好的凭据。无需粘贴密钥。 | **浏览器登录**<br>在内嵌窗口里登录一次，会话自动复用。 | **49 家服务商**<br>分组选择器、即时排序搜索、全键盘操作。 |
| **隐私开关**<br>一键遮蔽邮箱和余额，方便共享屏幕。 | **随你排序**<br>按套餐价值、重置频率或下次重置时间排序。 | **原生 Win11 体验**<br>Mica 材质、托盘图标、单实例、静默刷新。支持英文 + 简体中文。 |

## 安装

下载最新 **[release](https://github.com/SpookySandwich/QuotaLens/releases/latest)**：

| 文件 | 说明 |
| :-- | :-- |
| `QuotaLens-Setup-<version>-win-x64.exe` | 用户级安装程序，无需管理员权限。创建开始菜单项，桌面快捷方式可选。 |
| `QuotaLens-portable-<version>-win-x64.zip` | 解压后运行 `QuotaLens.exe`。 |

需要 **Windows 11 x64**。构建产物为自包含格式 —— 无需另行安装 .NET 或 Windows App SDK 运行时。

然后：启动即可。你已经登录过 CLI 的服务商会自动亮起来。其余的点 **Add Provider** 添加。看最上面那张卡片 —— 那就是现在该用的工具。

## 面板

**Recommended** 会按你自己的思路给套餐排序：优先推荐你已经在付费、且**仍有余量**的最高价值订阅，让钱先花在刀刃上。按量付费的 API 余额排在最后 —— 那是兜底选项，永远不会成为默认推荐。

**用量时间线**是一条覆盖全部已配置套餐的累计横条：

- 每一段代表一家服务商，**宽度是你本周估算的剩余 token** —— 不是百分比，所以 Max 20x 套餐哪怕只剩 5%，看起来仍然明显宽于一个满格的免费套餐。
- 各段按**恢复速度从慢到快**排列。左端是需要省着用的周额度池，右端是吃顿午饭就能回血的 5 小时池。
- **已消耗的容量不会绘制。** 这条横条表示剩余续航，而不是历史记录。

下方的服务商卡片提供分周期进度条、重置倒计时、绿 / 黄 / 红状态、信用余额，以及共享额度场景下的分账号明细。整组卡片可按套餐价值、重置频率或下次重置时间排序。

## 添加服务商

<p><b>49 家服务商，按所需配置方式分组：</b></p>
<ul>
<li><b>浏览器登录</b> —— 20 家。在内嵌窗口里登录一次；HttpOnly cookie 和响应由原生层捕获，所以登录完窗口是真的会关掉。</li>
<li><b>API key</b> —— 16 家。粘贴一个 key，读取余额。</li>
<li><b>本地应用或 CLI</b> —— 13 家。直接从工具已写好的凭据文件中读取（Claude Code、Codex、Gemini、Kimi Code）。</li>
</ul>
<p><b>Suggested</b> 分组会把你机器上大概率已经装了的服务商顶到前面。搜索是即时的且带排序；整个流程都能纯键盘完成。</p>

<details>
<summary><b>全部 49 家服务商</b></summary>

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

## 隐私

QuotaLens 是一个纯本地工具。没有后端，不需要注册账号，也没有任何遥测。它读取你机器上已有的凭据 —— CLI 写下的登录文件，或者你在内嵌浏览器里完成的登录 —— 然后请求各家服务商自己的用量接口，并把最近一次结果缓存在 `%LOCALAPPDATA%\QuotaLens` 下。除了访问你自己配置的服务商之外，没有任何数据离开你的机器。

你粘贴的 API key 以明文 JSON 形式保存在那里，依靠 Windows 用户账户的文件权限保护，而不是额外加密 —— 和你的 CLI 凭据文件本来就有的保护级别相同。如果某个 key 对你来说不能接受这种存放方式，建议改用能自动识别本地 CLI 登录的服务商。

内嵌登录窗口捕获了什么、以及刻意没有捕获什么，都在 [docs/web-login-capture-audit.md](docs/web-login-capture-audit.md) 中做了审计说明。

## 从源码构建

前置条件：Windows 11 和 .NET SDK。

```powershell
dotnet build .\QuotaLens.slnx -c Debug -p:Platform=x64
dotnet test  .\QuotaLens.slnx -c Debug -p:Platform=x64
```

630 个测试覆盖了额度计算、服务商响应解析、刷新调度以及推荐逻辑。在 `dotnet run` 后追加 `-- --ui-smoke`，可启动一个隔离的单窗口实例，不带托盘、刷新、网络请求和登录窗口 —— 在做 UI 自动化、不希望焦点被抢走时很有用。

在本地打包安装程序和便携版 zip（需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)）：

```powershell
.\scripts\package-windows.ps1 -Configuration Release -Platform x64 -Version 1.0.0
```

CI 会对每次推送执行构建和测试（`.github/workflows/windows-installer.yml`）。打 `v*` 标签或手动跑 Release 工作流会打 `1.0.0` 包并生成 GitHub release 草稿。

## 参考文档

| 文档 | 内容 |
| :-- | :-- |
| [plan-token-allowances.md](docs/plan-token-allowances.md) | 每一项每周 token 估算值、其来源及可信度 |
| [provider-plan-evidence.md](docs/provider-plan-evidence.md) | 各套餐价值排序背后的公开定价来源 |
| [provider-display-conventions.md](docs/provider-display-conventions.md) | 服务商卡片的标题与状态规则 |
| [web-login-capture-audit.md](docs/web-login-capture-audit.md) | 内嵌登录窗口具体捕获了哪些内容 |

## 致谢

Peter Steinberger 的 **[CodexBar](https://github.com/steipete/CodexBar)** —— 这款 macOS 菜单栏额度追踪工具的服务商实现和细致入微的逐家笔记，是 QuotaLens 用量 API 知识的主要来源。

Soju06 的 **[codex-lb](https://github.com/Soju06/codex-lb)** —— codex-lb 服务商所读取的共享账号负载均衡器。

两者均采用 MIT 许可证；详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

## 许可证

[MIT](LICENSE)
