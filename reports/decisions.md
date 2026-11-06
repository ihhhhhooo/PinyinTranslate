# 开发决定

- 平台：Windows 11 x64，.NET 10，WPF；候选读取和输入安全不做猜测。
- 2026-11-05 官方 DeepSeek 文档中 `deepseek-flash` 映射 DeepSeek-V4.1-Flash-1017，并支持图像。别名可能变化，不是固定快照。
- 预留按官方高峰、缓存未命中输入和高峰输出价格保守估计，失败及未知用量保留预留；任何额度不能因重启自动重置。
- 图片识别及翻译结果必须显示原图并要求核对，模型自行声称完整并不能证明没有遗漏。
- UI Automation 在独立无窗口 MTA 上订阅及读取；无法访问、密码框、多段或过长选区直接忽略。
- 用户指定测试凭据仅在本机以 DPAPI CurrentUser 解密；不复制到源码、报告或发行包。当前账户解密成功，明文未输出。
- 当前输入流程使用自动翻译与永久复制：保留约 0.3 秒的既有触发时间，用户看到“已复制”后手动 Esc、Ctrl+V。默认不再接管 Tab 或执行自动写入；本轮只做无桌面操作的模拟测试，详见 `auto-copy.md`。
- 2026-11-06 划词读取改用 UIA3 的真实选区和前台范围证据，覆盖非焦点的网页、文档提供者；辅以被动输入活动、去重检查及标准文本框原生只读选区接口。不向目标发送 Ctrl+C、不修改剪贴板来读文字；所有新回归均注入 fake，不控制桌面。详见 `selection-fix.md`。

参考：
- https://api-docs.deepseek.com/zh-cn/quick_start/pricing/
- https://api-docs.deepseek.com/zh-cn/guides/vision/
- https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading
