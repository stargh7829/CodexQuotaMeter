# Windows 实现说明

## 身份与请求

每轮从 Codex auth.json 捕获凭据。账号身份由工作区 account_id 与用户 sub 共同确定；凭据指纹使用完整登录内容的 SHA-256。AccountQuotaState 在凭据变化时增加代次，账号变化时清空额度。响应进入界面前再次读取凭据并同时检查代次和指纹。即便账号 A → B → A 后指纹回到原值，旧代次请求也无法写入。

同一账号更新令牌时可保留其最近成功快照并标记非实时。登录文件缺失、格式不完整或没有有效 ChatGPT token 时清空展示。API Key 登录及没有 auth.json 的系统凭据存储模式不提供此额度展示。

## 周期

WinForms 定时器周期为 60000 ms。程序启动和托盘“立即刷新”触发一次读取。每个定时周期检查当前账号并发起额度请求；超时为 8 秒。失败后等待下一周期，避免持续请求。请求完成后的凭据读取用于结果校验。所有网络操作在工作线程进行，UI 更新通过 BeginInvoke 回到界面线程。

## 窗口生命周期

悬浮窗独立于 Codex 的原生窗口所有权。Application.Run 保留悬浮窗消息循环，关闭或重建 Codex 窗口不会销毁悬浮窗。前台、移动及位置事件更新显示位置；非 Codex 前台自动隐藏。窗口具有 WS_EX_NOACTIVATE、WS_EX_TRANSPARENT 和 WS_EX_TOOLWINDOW，显示时不抢焦点。

Codex 识别采用进程名与可执行路径共同判断，支持 Microsoft Store 安装中实际进程名为 ChatGPT 的 Codex。标题栏位置按目标窗口 DPI 换算，为菜单和系统按钮保留空间。邮箱及每组额度、重置时间通过自绘控件渲染；顶部采用“邮箱 | 5h 百分比 HH:mm | 周 百分比 HH:mm”。空间不足时缩略邮箱，托盘菜单提供完整账号、日期和刷新状态。

## 安装与维护

setup.ps1 将发布文件复制到 %USERPROFILE%\AppTools\CodexQuotaMeter，通过当前用户交互会话的临时任务启动程序，由独立进程写入真实的当前用户 Run 启动项。临时任务定义在启动后移除，应用继续运行。再次运行用于升级。取消自动启动只移除本工具的启动项。脚本使用 PowerShell 7。

额度接口和本机文件格式属于 Codex 内部实现。维护时应验证真实响应，保持只读访问，并使用 RegressionTests.cs 检查账号切换、并发请求和窗口重建。


独立启动使用 Windows 任务计划服务的交互登录令牌模式，不需要保存 Windows 密码。参见 [Microsoft 的任务安全上下文说明](https://learn.microsoft.com/en-us/windows/win32/taskschd/security-contexts-for-running-tasks)。固定用户目录可避免从 Microsoft Store 版 Codex 执行脚本时发生 AppData 重定向；启动配置在独立进程中完成，以便 Windows 登录启动读取到正确的注册表项。

