# QuotaLens

开发说明 · 版本始终是 1.0.0

给改代码的人。产品行为以 [使用说明](user-guide.md) 为准；这里只写怎么编、怎么测、配置页约定。

## 构建与测试

Windows 11，.NET SDK（目标框架 `net10.0-windows`）。

```powershell
dotnet build .\QuotaLens.slnx -c Debug -p:Platform=x64
dotnet test  .\QuotaLens.slnx -c Debug -p:Platform=x64
```

当前 727 个测试，覆盖额度计算、平台解析、刷新调度、加号流程和推荐逻辑。

## 打包

需要本机安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)。版本号写死 **1.0.0**，不要加 1.0.1 / 1.1.x。

```powershell
.\scripts\package-windows.ps1 -Configuration Release -Platform x64 -Version 1.0.0
```

## CI

每次推送只跑构建和测试（`.github/workflows/windows-installer.yml`）。安装包不在每次 push 打。打 `v*` 标签或手动跑 Release 工作流，才会打 1.0.0 安装包和便携版并生成 GitHub Release 草稿。

## 配置页

每个可添加平台都必须走同一张配置页。`ProviderAddFlow` 不再按 BrowserLogin / ApiKey / Local 分叉。登录按钮只出现在需要浏览器会话（或 CLI 登录器）的源上。选 App / CLI 时读本机会话，不要再弹一次网页登录。点「完成」必须先 Fetch 成功，否则对话框留下。

## 平台无关的数据架构

平台适配器只负责把各家的响应转换成 `ProviderSnapshot`：重置时刻写入 `ResetsAt`，周期写入 `WindowMinutes`，模型配额写入 `ModelQuotas`。卡片、时间线、可用率和排序逻辑只读取这些结构化字段，不得根据平台 ID 改分支。

所有卡片重置文案由 `ResetFormatter` 生成，例如 `resets in 3h 12m`。只要 `ResetsAt` 有效，就不显示平台返回的重置句子；`DetailText` 仅保留用量、余额、状态等非重置说明。

多数据源统一实现 `IProviderSource`。`ProviderSourceRunner` 在用户明确选择源时严格使用该源；未选择时才按顺序自动回退。源通过 `UnavailableRecovery` 声明无数据时的恢复动作，通过 `WatchPaths` 声明会话文件；刷新服务和卡片不探测平台类型。配置字段改名统一放进 `Catalog.ConfigKeyAliases`，由配置服务迁移全局或实例字段。

新增平台特殊性应留在解析器或 Catalog 数据中。不要在共享视图、刷新、排序、恢复或监视代码中增加平台名称判断。

未做事项见 [todo.md](todo.md)；近期系统与接口修改见 [recent-changes.md](recent-changes.md)。

## 卡片标题

卡片标题只说明这是谁，不说明出了什么事。有套餐写「平台 · 套餐」，没有套餐或已过期只写「平台」。`expired`、`login required`、`trial ended` 这类状态放在卡片正文，不要写进标题。
