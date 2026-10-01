# Codex 额度悬浮条（Windows）

在 Codex 窗口顶部显示当前账号和剩余额度。中文界面，支持多账号切换、Codex 窗口重建以及随 Windows 自动启动。

[下载 Windows 安装包](https://github.com/stargh7829/CodexQuotaMeter/releases/latest/download/CodexQuotaMeter-Windows-v1.1.0.zip) · [查看版本](https://github.com/stargh7829/CodexQuotaMeter/releases)

## 一键使用

1. 下载并完整解压安装包。
2. 双击 **一键配置.bat**（或 `setup.bat`）。
3. 程序安装到 `%USERPROFILE%\AppTools\CodexQuotaMeter`，立即运行并配置当前用户登录 Windows 时自动启动，无需管理员权限。

以后打开 Codex 就会自动显示额度条。安装完成后可以删除下载的压缩包和原解压目录。

配置脚本使用 **PowerShell 7**（`pwsh.exe`）。如果电脑尚未安装 PowerShell 7，也可直接运行 `CodexQuotaMeter.exe`，通过托盘右键菜单勾选“随 Windows 启动”；采用这种方式时请保留程序所在目录。

## 看什么

显示顺序为：

**12345@qq.com | 5h 80% 21:13 | 周 81% 06:41**

- 剩余 ≥ 60%：绿色；20% ≤ 剩余 < 60%：黄色；剩余 < 20%：红色。
- 剩余百分比加粗放大，账号和重置时间保持普通字号。
- 账号、5h、周采用三个明确留白的区域，以细分隔线隔开。5h 和周各自的百分比、重置时间排在同一组。顶部只显示邮箱、窗口简称、百分比和 HH:mm；托盘菜单提供完整重置日期和刷新状态。标题栏较窄时邮箱会缩略。
- 额度窗口按接口实际返回的时长显示。某些套餐的窗口并非 5h/一周，程序会显示实际时长。

## 账号与刷新

- 启动时立即刷新，此后**账号检查和额度请求统一每 1 分钟一次**。请求失败也在下一分钟重试。
- 每轮读取 Codex 当前登录文件 `auth.json` 中的用户及工作区身份；切换账号后最迟在下一轮刷新跟随新账号。
- 检测到切换或退出登录时清空旧账号额度；每个请求绑定账号和请求代次，旧请求晚到时会被丢弃。
- 同一账号网络失败时保留上次成功结果，并在托盘菜单注明“刷新失败，显示上次数据”。
- 切换账号时不使用其他账号的历史日志额度。新账号获取失败时顶部显示占位符，托盘菜单注明同步失败。
- Windows 窗口事件负责移动、前台切换和窗口重建后的恢复，不额外请求额度。关闭 Codex 后程序留在托盘，重新打开 Codex 会恢复。
- 悬浮条不接管鼠标或键盘，Codex 在后台或最小化时自动隐藏。

## 运行环境

Windows 10/11 x64，.NET Framework 4.8。应用使用 Windows 自带框架，不需要 Node、Python 或额外运行中的服务。

默认使用 `%USERPROFILE%\.codex`。自定义 Codex 数据目录可通过 `CODEX_HOME` 指定；本工具专用覆盖变量为 `CODEX_QUOTA_DATA_DIR`。这些变量应指向当前桌面客户端实际使用的目录。

## 隐私

程序只读访问 Codex 当前登录文件和外观配置，并向 `https://chatgpt.com/backend-api/wham/usage` 请求额度。令牌仅在内存中用于 OpenAI 的额度请求；不收集遥测，不保存或上传账号列表，不刷新或修改 Codex 登录令牌。

邮箱通过本机登录令牌的声明获得，只用于本地界面展示。额度接口属于内部接口，Codex 后续版本变化可能需要更新本工具；如果登录使用系统凭据存储且没有可读的 `auth.json`，当前版本会显示等待登录。

## 取消自动启动

双击 `取消自动启动.bat` 或 `uninstall-autostart.bat`，也可在托盘菜单取消“随 Windows 启动”。需要退出时选择托盘菜单的“退出”。

## 源码构建与验证

在 PowerShell 7 中运行：

```powershell
.\build.ps1
.\test.ps1
.\package.ps1
```

构建使用 Windows 自带的 .NET Framework C# 编译器，打包产物位于 `dist`。回归测试使用虚构凭据和模拟窗口，覆盖账号切换、请求竞态、登出、窗口重建及颜色阈值。可选 `test.ps1 -Live` 验证当前账号的真实额度请求，仅输出是否匹配和窗口数量。

## 开源来源

基于 [Zoey Liew 的 codex-usage-remaining](https://github.com/zoeyliew192/codex-usage-remaining) 的 Windows 实现改造，采用 MIT 许可并保留原版权声明。当前版本的中文展示、账号绑定状态机、独立窗口生命周期及安装脚本位于本仓库。



