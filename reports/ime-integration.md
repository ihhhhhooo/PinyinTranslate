# 微软拼音输入翻译验收记录

日期：2026-11-05。环境：当前 Windows 11 x64 桌面。所有输入测试使用本项目独立样例窗口；本轮没有操作 Edge。

## 已实现的行为

系统微软拼音负责拼音、候选和词库。开启中译英模式后，程序读取当前页第一个可见候选，在其上方显示一个英文译法。普通 Tab 接受；等待翻译期间按 Tab 会排队，后续输入、鼠标位置意图、焦点或模式变化会撤销接受。带修饰键的 Tab 走普通快捷键路径。

候选读取在独立 ImeBridge 进程中进行。当前用户管道验证进程身份，并使用会话、模式和输入序号阻止旧消息复活。预览窗口不激活、不占用编辑焦点。

写入前重新检查候选、控件身份、完整正文和折叠光标。先用 Esc 结束本轮拼音，再精确确认组合前正文和插入点；WPF TextBox 使用 ValuePattern 写入并恢复光标，已验证的 WinForms 控件使用 Unicode 输入。失败不猜测退格数、不重发译文。已写入后的取消允许受独立位置版本保护的光标收尾。

## 桌面实测

| 场景 | 结果 |
| --- | --- |
| WPF TextBox：真实候选“是”与英文预览，Tab 接受 | 通过，保留原文和焦点 |
| WPF TextBox：`shu'yu`，第一候选“属于”，正文中间接受 `belong to` | 通过，前后缀及插入末尾光标一致 |
| WinForms TextBox：组合是覆盖层，UIA 正文保持基线，候选“是”→`is` | 通过 |
| WinForms TextBox：连续第二轮“属于”→`belong to` | 通过，完整输入多词英文 |
| WinForms RichTextBox：候选、预览及 Tab 输入 | 通过，处理已知的正文末尾终止符差异 |
| 模拟慢网络：译文返回前按 Tab | 通过，等待后仅写入一次 |
| 排队后继续输入，等待迟到译文 | 通过，仅更新预览，未自动写入 |
| 排队后鼠标切换到同一窗口的另一输入框 | 通过，两个输入框未收到迟到英文 |
| 模拟服务失败 | 未写入英文，原已提交正文保持不变 |
| 模式关闭后普通 Tab | 通过，正常切换编辑焦点 |
| 合成密码框 | 不建立候选绑定，不显示翻译预览 |
| 真实 DeepSeek：RichTextBox 中“属于”→`Belongs to` | 通过，结果为 `PREFIX Belongs to\| SUFFIX` |
| 真实 DeepSeek：WPF 输入翻译与划词模式同时开启 | 通过，“属于”→`Belongs to`，原句保留 |
| 同时开启两模式，选择独立英文样例 | 通过，返回“这些样本属于这一组。” |

真实请求使用 `deepseek-flash`、关闭思考，经过同一开发预算账本。本轮新增 7 次推理尝试，均返回用量，本地估算已知费用 ¥0.001036；任务累计 21 次、¥0.010922，低于 200 次／¥1 限额。该数字是本地估算，不是服务商账单。

## 自动检查与发行检查

`scripts/test.ps1` 最终结果：Core 112、Selection 11、Screenshot／桌面逻辑 17，共 **140 通过，0 失败，0 跳过**。

新增首词回归测试把候选观察器停在未就绪状态，验证第一键已经保存组合前基线；随后完成 Ready，确认基线仍保留。该测试使用受控观察器，不注入系统输入。写入、IPC 代次、重复接受、Unicode、原生覆盖层和光标收尾另有自动化测试。

发布程序为 win-x64 .NET 10.0.14 自包含包。主程序已在当前桌面启动，主题和已有配置正常读取；发行目录内的 ImeBridge 已单独启动并读取、复核真实微软拼音候选。发行检查确认主程序、后台程序及其 DLL／运行配置齐全，未包含测试密钥、DPAPI 凭据、测试会话或诊断日志。

## 验证范围

目前开放并验证标准 WPF TextBox、WinForms Edit／RichEdit。横排微软拼音候选已实测；浏览器、Office、Java／Electron 等其他编辑器没有在本轮逐一开放或验收，不能据此宣称所有 Windows 输入场景均可写入。混合 DPI、多屏、提升权限目标及干净机器尚未做完整兼容验证。

WPF 的整段文本接口可能改变撤销历史。跨进程检查和实际写入之间仍存在时间间隔；收到失效或不确定结果时停止重试。模式开启时已经存在的组合可能需要先结束，再开始新输入。早期 `ime-prototype.md` 的“未接入”结论为历史记录，以本文所述版本和验证范围为准。

微软接口参考：[微软拼音按键说明](https://support.microsoft.com/en-us/windows/hardware/input-devices/microsoft-simplified-chinese-ime)、[SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[TextPattern](https://learn.microsoft.com/en-us/dotnet/framework/ui-automation/ui-automation-textpattern-overview)。
