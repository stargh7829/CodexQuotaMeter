# 隐私与数据访问

应用读取当前用户的 Codex auth.json，以内存中的访问令牌及账号标识请求 OpenAI 的额度接口：https://chatgpt.com/backend-api/wham/usage，以及个人资料接口：https://chatgpt.com/backend-api/wham/profiles/me。账号邮箱从本机登录令牌的声明读取；用户名从当前账号个人资料的 profile.display_name 读取，只用于本地显示。应用不把登录令牌中的 name 或个人资料的 username 账号标记替代为资料显示名。

应用读取 config.toml 的外观选项。应用不写入 Codex 的登录文件，不保存账号或令牌，不读取历史额度日志，不收集遥测，不向项目作者的服务器发送数据。网络失败只保留内存中同一账号的最近成功额度和已核验的昵称；切换或退出账号会清空数据。个人资料请求失败时额度仍可正常更新，自动请求统一每分钟执行一次。

一键配置脚本把程序复制到当前用户的本地应用目录，并在 HKCU\Software\Microsoft\Windows\CurrentVersion\Run 写入 CodexQuotaMeter 自动启动项。取消自动启动脚本删除这个启动项。

诊断命令 --snapshot 会在本机控制台输出账号及额度；请在向他人分享输出前去掉邮箱等个人信息。回归测试默认使用虚构凭据，可选的真实请求检查仅输出匹配结果和额度窗口数量。

代码仓库及发行包包含源码、程序、安装脚本、说明和许可证；不包含用户登录文件、令牌、账号列表或个人日志。

