# 参与 AgentLock

AgentLock 是 Windows 当前桌面的本地守护工具。使用 Windows 与 .NET 10 SDK；构建和运行方法见 [README](README.md)。

## 当前改动范围

0.1.5 新增本人 Windows 登录后的忘密恢复，这是在 0.1.4 视觉基线上的独立功能改动。输入钩子、F12 与密码哈希保持原策略；新增授权必须依赖当前会话的真实锁定 / 解锁查询，不能把请求获准或系统消息当作本人认证。其他新增功能或保护语义变化作为独立改动讨论。

界面改动遵循 [视觉规范](docs/视觉设计.md)，保留可读状态、键盘操作、缩放与小窗口适配。截图必须来自实际程序，不用设计稿充当运行证据。

## 验证改动

```powershell
dotnet run --project .\tests\AgentLock.Tests\AgentLock.Tests.csproj --configuration Release
```

测试使用纯策略、假恢复后端和独立临时文件，不安装全局输入钩子、发送输入、真实锁屏或读取正式密码。

恢复监控脚本只运行 dry-run，使用自己创建的临时文件与进程：

```powershell
.\tests\Test-Watchdog.ps1 -ExePath .\release\AgentLock\AgentLock.exe
```

内置诊断和 GUI 检查另行记录测试范围。自有测试窗通过不能替代真实 Agent 或实体键鼠验收；需要它们时按 [验收清单](docs/验收清单.md) 留存版本、设备与结果。

## 提交说明

说明具体问题、调整后的行为及实际验证。视觉调整附真实界面截图，注明 Windows 缩放；尚未验证的路径明确列出。测试以能发现错误的回归为主，不为颜色或排版编写镜像测试。

不要提交 `.tools/`、构建 / 发布产物、`artifacts/`、凭证、心跳、个人日志、诊断截图或账户信息。公开截图放入 `docs/images/` 前确认无敏感内容；保留源 SVG 和字体回退，不引入联网字体。

许可证尚待项目所有者决定；目前不自行添加许可证、远程发布或扩大授权范围。
