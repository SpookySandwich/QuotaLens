# QuotaLens

使用说明 · 版本 1.0.0

一眼看出现在该用哪一个 AI 编程工具。本地 Windows 仪表盘，跟踪你已经付费的额度、用量和余额。没有后台，没有遥测。

## 安装

需要 **Windows 11 x64**。安装包是自包含的，不必再装 .NET 或 Windows App SDK。

| 文件 | 说明 |
| --- | --- |
| `QuotaLens-Setup-1.0.0-win-x64.exe` | 当前用户安装，不需要管理员。开始菜单，可选桌面快捷方式。 |
| `QuotaLens-portable-1.0.0-win-x64.zip` | 解压后运行 `QuotaLens.exe`。 |

从 GitHub Releases 下载最新 1.0.0。界面支持 English 和简体中文。

## 添加平台

点「添加平台」，选一个名字。每一个平台都会打开**同一张配置页**，不会因为类型不同而跳过。

- **浏览器登录**（Cursor、OpenCode、Amp 等）：配置页上点「登录」，在内嵌窗口签一次名，再点「完成」。
- **本机应用 / CLI**（Claude Code、Codex、Kimi App、Gemini 等）：确认路径即可。已经在软件里登录过的，不必再登一遍。
- **API Key**（DeepSeek、OpenRouter 等）：把密钥贴进配置页，再点「完成」。

「完成」会先真正拉一次额度。失败就停在配置页并说明原因，不会放出一张坏卡片。卡片上没有登录按钮；以后要改设置，点铅笔，还是这张配置页。

## 看仪表盘

- **推荐**：在已经付钱、还有余量的计划里，挑最值得先用的那个。按量付费的 API 余额排在最后，不当默认选择。
- **用量时间线**：一条累计条。每段宽度是本周大概还剩多少 token，不是百分比。从补得最慢的排到最快的。已用掉的部分不画。
- **卡片**：各窗口进度条、重置倒计时、状态色、余额。可按计划价值、重置频率或下次重置排序。

## 隐私

QuotaLens 只在本机运行。它读取你机器上已有的凭据（CLI 写过的文件，或你在配置页登录留下的会话），向你配置过的平台自己的用量接口发请求，并把上次结果缓存在 `%LOCALAPPDATA%\QuotaLens`。除了这些请求，没有数据离开这台电脑。

贴进去的 API Key 以明文 JSON 存放，保护方式和 CLI 凭据文件一样：Windows 用户目录权限，没有额外加密。

## 48 个平台

Abacus AI · Alibaba · Amp · Augment · AWS Bedrock · Azure OpenAI · BayesDL · Claude Code · Codebuff · Codex · codex-lb · Command Code · Crof · Cursor · DeepSeek · Deepgram · Doubao · ElevenLabs · Factory · Gemini · GitHub Copilot · Grok · Groq · JetBrains AI · Kilo · Kimi · Kiro · LLM Proxy · Manus · MiMo · MiniMax · Mistral · Moonshot · Ollama · OpenAI API · OpenCode · OpenCode Go · OpenRouter · Perplexity · Qoder · StepFun · Synthetic · T3 Chat · Venice · Vertex AI · Warp · Windsurf · z.ai
