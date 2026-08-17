# QuotaLens · 最近变更 (Recent Changes)

本文档记录 QuotaLens 核心功能、数据适配、平台解耦及稳定性方面的近期修改与优化。

---

## 1. Gemini 额度展示升级与多模型分组适配

- **速率窗口重构 (Rate Windows Architecture)**:
  - 适配 Google / Gemini 语言服务返回的全新分组速率窗口格式：
    - **Gemini Models**:
      - `Weekly Limit Remaining` (每周额度余量；服务端重置时间统一显示为 `resets in 3h 12m` 一类紧凑格式)
      - `Five Hour Limit Remaining` (5小时速率额度余量)
    - **Claude and GPT models**:
      - `Weekly Limit Remaining` (每周额度余量)
      - `Five Hour Limit Remaining` (5小时速率额度余量)
  - 调整卡片条目排列顺序：每周周期额度 (`Weekly`) 置于首位，短周期 (`5-Hour`) 置于下方。
  - 数据源优先级升级：优先通过本机的 `Antigravity local probe` 探测活动语言服务器获取完整分组数据，并保持对旧版 CLI 单模型输出的向下兼容。

---

## 2. Gemini 启动解耦与自定义应用路径

- **解耦 AntiGravity 绑定**:
  - 移除 Gemini 启动目标中对 `Antigravity IDE.exe` 的硬编码依赖，卡片启动名称规范为 `Gemini`。
  - 在配置页字段中将原项更新为通用「桌面应用路径」(`Desktop app path` / `gemini_app_path`)。
  - 用户可自由配置自定义编辑器或桌面端可执行程序路径；留空时自动回退至系统默认文件编辑器。
  - 补充并完善中英文国际化词条 (`I18n.cs`)。
  - 完成并核销 `docs/todo.md` 中的对应待办事项。

---

## 3. 本地语言服务器探测与进程管道稳定性修复

- **系统进程绝对路径调用**:
  - `AntigravityProvider.cs` 的 `TryDiscoverAsync` 改为使用系统明确路径调用 `%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe` 与 `%SystemRoot%\System32\cmd.exe`，避免受环境工作目录或 PATH 劫持影响。
  - PowerShell 脚本执行改用 `-EncodedCommand` (Base64 Unicode)，彻底规避管道符号 `|` 与引号嵌套的转义解析问题。
- **进程重定向死锁修复 (Process Stderr Deadlock)**:
  - 在 `RunProcessAsync` 中同时异步清空标准输出 (`stdout`) 与标准错误 (`stderr`)，解决子进程因 stderr 缓冲区填满导致 `WaitForExitAsync` 挂起阻塞的经典死锁缺陷。
- **精确 PID 块解析**:
  - 重构 `ParseProcs`，按每个 `ProcessId` 的输出区间独立解析对应的 `--csrf_token`，杜绝多进程并发时 CSRF 令牌跨进程错配。

---

## 4. Grok CLI 进程重定向初始化修复

- **标准输入重定向前置 (StandardInput Redirection)**:
  - 修复 `GrokProvider.cs` 在调用 `HiddenCliProcess.CreateStartInfo` 后，未先将 `RedirectStandardInput = true` 即赋值 `StandardInputEncoding = Encoding.UTF8` 导致的 .NET `InvalidOperationException: StandardInputEncoding is only supported when StandardInput is redirected` 异常。
  - 本机未安装 Grok CLI 时，规范提示 `Not available: Grok CLI not found at grok`，卡片状态准确展示。

---

## 5. 系统托盘异常防护

- **托盘初始化防崩溃**:
  - 在 `TrayService.cs` 的 `BuildTrayIcon` 中对 `_trayIcon.ForceCreate()` 添加异常保护与日志记录。
  - 即使在特殊桌面会话或 Explorer 托盘区域未就绪时，也确保不会抛出未捕获异常导致整个应用程序崩溃，保障主界面渲染与后台额度刷新正常工作。

---

## 6. 平台无关的配额、数据源与恢复架构

- **统一重置文案**:
  - `RateWindow` 只承载结构化 `ResetsAt` / `WindowMinutes` 和非重置说明 `DetailText`。
  - `ResetFormatter` 是卡片唯一的重置文案入口，Codex、codex-lb、Claude、Gemini、Antigravity 等不再各自拼接句子或泄漏 ISO 时间。
- **统一多源选择**:
  - 所有多源平台通过 `IProviderSource` 和 `ProviderSourceRunner` 选择数据源。
  - 用户明确选 App / CLI / Web 时严格使用该源；只有自动模式允许回退，避免 Kimi App 被健康但缺少总额度的其他源静默替换。
- **Kimi App 总额度与会话恢复**:
  - App 源直接解析 `totalQuota`，并与 Weekly、5h Rate Limit 一起展示。
  - 通用 Electron safeStorage 读取器使用 Windows DPAPI 解包密钥并解密 Chromium `v10` / `v11` AES-GCM 数据，无需仅为读取会话而打开 Kimi 窗口。
  - Kimi 的短期 token 仍由官方应用在实际活动时轮换；QuotaLens 不伪造刷新流程。通用文件监视器会在会话文件变化后自动重新拉取；「打开应用恢复」只在选定源确实无数据的错误卡片出现。
- **共享机制去平台判断**:
  - 模型分组、总体可用率、重置优先级、恢复按钮和会话文件监视都读取结构化快照或 Catalog 数据，不再按平台 ID 写例外分支。
  - 旧配置键通过 `Catalog.ConfigKeyAliases` 的通用迁移保留用户设置。

---

## 7. 测试与构建

- **单元测试**:
  - 完善 `AntigravityProviderTests.cs` 和 `GeminiProviderTests.cs`，涵盖新版分组额度、周期排序及向后兼容性验证。
  - 全部 727 个测试顺利通过 (`dotnet test .\QuotaLens.slnx -c Debug -p:Platform=x64`)。
- **打包安装**:
  - 严格保持版本号为 `1.0.0`。
  - 生成 Release 安装程序 `QuotaLens-Setup-1.0.0-win-x64.exe` 与便携版压缩包。
