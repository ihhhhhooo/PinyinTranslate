# 通用写入与独立预览（2026-11-05）

## 本轮范围与结果

按用户要求实现候选翻译预览独立、通用自动粘贴、准备阶段 Unicode 备选以及结果核对。测试不允许控制用户电脑：本轮没有启动产品或真实输入窗口，没有操纵窗口、安装全局输入钩子、读取实际 UIA 或系统剪贴板，也没有发送真实键鼠事件。没有进行真实翻译 API 调用或读取用户提供的真实凭据。

`scripts/test.ps1 -HeadlessOnly` 通过：Core 150、Selection 11、桌面逻辑 29，共 **190 项通过、0 失败**。唯一未执行的可选测试为 `CoreTests.OptionalProvidedCredentialCanBeDecryptedWithoutPrintingIt`，因其可能读取环境指定的真实凭据而显式过滤；测试运行器本身报告 skipped=0。相比原有测试范围，本轮新增 51 个 case。

这些结果证明纯逻辑、事务顺序与合成场景通过，不能证明新版在任意浏览器、聊天软件或微软拼音真实输入中的兼容性。上一份 `ime-integration.md` 的真实 WPF/WinForms 验证属于以前版本。

## 已实现

### 独立预览

- `CanPreview` 与 `CanReplace` 分离。输入身份可识别且不是密码框时，可读取实际候选并翻译；正文不完整、没有可靠光标、只读或缺少写入能力不再直接压掉预览。没有真实当前候选时不会生成翻译。
- Runtime 的发布允许空基线；只有接受 Tab 时才要求可写、非空基线、基线与当前候选状态匹配及全部代次、焦点检查通过。
- 仅预览显示“仅预览，暂不可自动输入”，不拦截普通 Tab。

### 写入路径

1. 已有 WPF / WinForms 专用路径继续使用原有方法。
2. 其他 UIA Edit/Document 控件按能力判断，具备完整一致正文、明确可写状态、单一折叠光标和稳定身份时选择通用粘贴。
3. Tab 接受后先发送一次带自身标记的 Esc，等待两次精确基线复核；不猜测删除数量、不提交中文再删除。
4. 内联拼音要求观察到的 ASCII 拼音确实被取消并恢复原文与光标。未知覆盖式组合不能凭“候选框消失”写入；有 Active 状态的覆盖式组合必须在取消后取得 Inactive 证据。
5. 粘贴前、剪贴板准备后、实际发键前再次检查目标、基线、组合状态和取消标记。
6. 系统剪贴板临时放入译文，发送一次带自身标记的 Ctrl+V，核对正文和最终光标。正文不通过整框 SetValue 改写。
7. 只有剪贴板准备阶段失败且尚未发送键盘输入时，才尝试一次 Unicode 批次。部分发送、异常或结果不确定均不触发第二次输入。

### 剪贴板与键状态保护

- 剪贴板操作的 message-only owner 在后台线程运行，无可见或激活窗口；构造后端没有系统副作用，只有正式接受译文才创建会话。
- 清空前备份有界且可恢复的内存格式。未知私有格式、图片等非内存句柄、过多格式或超限数据使准备失败并保留原内容。
- 准备与恢复均拒绝零序列号。发送粘贴前核对当前 owner 和非零序列号仍属于本次译文；用户或其他程序换过剪贴板时取消本次粘贴，保留新内容。
- 只有确认译文已输入，或能确认未发送输入时，才尝试恢复原剪贴板；恢复时在打开剪贴板的范围内再次核对 owner 和序列号。
- 粘贴尚未核对时保留译文，避免延迟执行的 Ctrl+V 读取已经恢复的旧内容；主界面显示相应提示。
- Ctrl+V 部分发送时只对本次已经接受的合成 down 发送一次 key-up 收尾，绝不重发正文；Esc 的部分发送也只释放本次 Esc-down。

## 无桌面操作的测试设计

- `EditableContextCapabilitiesTests`：合成控件支持、预览与写入分离、密码/只读/不完整文档/不一致正文/选区限制、专用路径优先、内联取消与覆盖式组合证据。
- `GenericTextInsertionTests`：fake clipboard、keyboard 与 session，验证粘贴、Unicode 备选、撤销、最后边界更换剪贴板、部分输入、异常、恢复顺序、用户新复制内容及仅 key-up 收尾计划。
- `ImePreviewCapabilityTests`：通过 uninitialized Runtime 和托管委托调用真实状态逻辑，覆盖无基线预览、Tab 透传、泛型组合接受门槛、失焦/按键/指针取消、等待译文与重复 Tab 合并、临写前上下文变化、用户提示传递。
- 原启动绑定测试改用同一 headless 夹具，不再构造真实 Runtime、UIA worker 或全局钩子。生产构造与生命周期保持正常实现；测试注入的 reader、writer、foreground 与 translator 全部为纯 stub。
- headless 脚本先核对 16 份已静态审查的测试/辅助源码，新增未知文件时失败而不是静默跳过。所有新测试均未调用真实 clipboard、SendInput 或 UIA。

## 实际兼容边界

当前仍要求焦点所在的 UIA Edit/Document 与前台进程一致。不能获得可靠身份的自绘控件、密码框、高权限目标、超出 8192 UTF-16 单元的正文、不可核对的多选区或组合状态，都不能据此获得自动写入保证；部分场景可只预览。

通用粘贴是否被具体应用接受、UIA 是否准确公开组合、Ctrl+V 后的真实行为和混合 DPI，均未在本轮实际应用中验证。系统剪贴板与 SendInput、焦点变化之间没有跨进程原子事务；发送前检查能缩小竞态窗口，不能消除它。底层剪贴板渲染可能阻塞；当前同步操作没有添加会导致后台晚执行写入的假超时。

参考：[Unicode 模拟输入](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)、[SendInput 与权限限制](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)、[组合状态查询](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition)、[剪贴板序列号](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)。
