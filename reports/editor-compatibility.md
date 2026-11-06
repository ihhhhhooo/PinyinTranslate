# 终端与 IDEA 的读取兼容修复

这些兼容调整先在本地完成，现经用户明确授权随 1.0.6 发布。正式交互仍是停顿约 0.3 秒翻译微软拼音首候选并复制，然后手动 Esc、Ctrl+V；没有恢复 Tab 拦截或向终端发送输入。

## 发现的问题与修改

1. 原 `EditableContextAdapter.ReadFocusedAsync` 在能力判断之前直接拒绝非 Edit/Document 控件。终端的 Text、自绘编辑区或 Java 焦点代理因此可能根本没有机会绑定候选观察器。正式自动复制模式现在改用只读的 `ReadPreviewFocusAsync`，不检查控件角色，不读取代码或终端全文，也不要求写入或光标接口。主程序和独立候选观察器共用同一套焦点解析，核对进程、RuntimeId、原始树祖先、密码标记和前台窗口；旧写入路径仍保留原有严格要求。
2. 鼠标命中终端外层容器时，真实 TextPattern 可能在子节点，而原划词算法只沿祖先读取，随后直接结束。现在增加容器内受限查找；鼠标选区必须有包含手势位置的边界，不能改读另一个窗格的旧选区。明确的空光标、受保护节点、多个独立选区来源及未完成的树遍历仍然拒绝；已提供真实选区的节点不会再被其内部文本代理重复计数。
3. 原划词实现仅连接 Windows UIA 与原生 Edit/RichEdit 消息，缺少 Java Access Bridge。增加只读 Java 选区后端，针对前台 SunAwtFrame/SunAwtDialog/SunAwtWindow 使用 Java 真实选区；已有 UIA 路径仍作为回退。根据鼠标目标或 Java 焦点查找文本节点，检查密码角色、显示状态、窗口归属、Java 对象身份、起止位置及两次一致的文本；上限仍为 4000 个 UTF-16 单元。原生选区摘要只有 1024 字符缓冲，长选区改读准确的范围，不截断摘要。
4. 后续发现拼音路径仍把前台宿主 PID 和编辑区 PID 必须相同作为限制。现在允许经过原始树祖先核对、确实属于前台窗口的其他进程编辑区做候选预览；不会因此授权旧写入器。宿主窗口、宿主 PID、编辑区 PID、RuntimeId 分开绑定，IPC 协议升级为 3，主程序与候选服务仍须配套。焦点和候选 UIA 设置 250ms 的连接/事务超时；不能核对焦点时按窗口提示可用的兼容方式，避免一直无反馈。UIA 的超时配置不能视为任意底层原生调用都能被强制中断的保证。

Java 客户端按需从目标运行时的 bin、jbr/bin、jre/bin 或 System32 加载 x64 WindowsAccessBridge DLL，不从当前目录或 PATH 查找，不附带或下载 DLL。专用线程维护桥接消息循环及所有 Java 引用，读取有等待上限且禁止堆积；关闭时由原生所有者释放资源。没有修改 IDE 配置、执行 jabswitch、自动发送 Ctrl+C、改写剪贴板或改动目标内容。Java 接口未开放或不可加载时，完成选择手势后显示具体提示。

## 经用户确认加入的手动复制兼容方式

用户同意将“选中后手动 Ctrl+C 触发翻译”作为无法读取选区时的补充，普通自动划词继续保留。该路径复用划词开关、语言对、翻译缓存、统计和结果展示，不读取输入框全文、不自动按 Ctrl+C，也不恢复 Tab 写入。

- 仅在划词模式开启后，先完成前台窗口的鼠标/键盘选择动作，再亲自按 Ctrl+C 或 Ctrl+Shift+C；选择动作有效期 15 秒。按键钩子在应用收到复制键之前只捕获剪贴板序号，不读取内容。等 C 和修饰键释放后才启动读取，避免它们的松键取消新请求。注入按键、其他窗口的动作、旧模式代次和没有复制前序号的事件不授权读取。
- 只处理序号明确更新、本次前台窗口仍一致的 Unicode 文本；剪贴板所有者须能对应前台宿主、其同窗口的子进程/焦点，或由系统报告为未知。排除本程序产生的剪贴板更新和已知原生密码框。不翻译旧剪贴板、图片、空文本、无效 UTF-16 或超过 4000 个 UTF-16 单元的内容。序号未更新（包括未能取得更新证据的延迟渲染）时不会把旧内容当成复制成功。
- 输入译文复制器会销毁临时所有者窗口，因此本程序的已复制内容也可能显示为“无所有者”。现在在仍持有剪贴板锁时记录自身成功写入的序号；兼容读取另排除这些序号，结果窗口和统计页的真实复制也做标记。注册表仅在内存保存最近 64 个序号、保留约一分钟，不包含文本。模拟写入默认不读取真实序号，测试没有调用系统剪贴板。
- 不依赖 UIA 线程；连 UIA 初始化或读取不响应时，手动复制仍可建立独立请求。读取只在明确复制后启动；单个后台工作线程串行处理、拒绝积压，调用方约 1.2 秒后结束等待，监控层另有 2 秒上限。打开剪贴板前用有界 WM_NULL 检查已知所有者的响应状态；不改变目标输入。原生 GetClipboardData 仍可能受 Windows 延迟渲染阻塞，已进入的系统调用不能被托管取消强制终止，工作线程须等它返回后释放资源。
- 复制的文字是明确的文本快照，不冒充仍能实时读取的 UIA 选区。捕获后不再读取剪贴板正文；新的操作、窗口变化、关闭模式、截图暂停、改变语言对或更新准备会使旧请求失效。复制结果本身不会再次触发翻译。共享缓存避免已完成的同文划词/复制重复调用接口。

该方法扩大可用范围，不保证任意软件、受保护界面或不支持复制的内容可用。拼音自动预览仍需能够核对非密码焦点并读到当前微软拼音候选；不开放该接口时，可先把中文上屏，再选中并复制来翻译，或使用截图。

官方依据：[Text 控件不强制提供 TextPattern](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-supporttextcontroltype)、[剪贴板序号](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getclipboardsequencenumber)、[Windows 剪贴板操作与延迟渲染](https://learn.microsoft.com/en-us/windows/win32/dataxchg/clipboard-operations)。未验证当前微信具体提供了哪些接口，因此没有把通用代码缺口当作已确认的微信唯一原因。

## IDEA 的前提

- IDE 必须开放文本无障碍接口。必要时在 IDEA 设置中搜索并开启 **Support screen readers / 支持屏幕阅读器**；不同版本所在设置分组可能不同。
- Java Access Bridge 也需启用。Oracle 文档提供运行时 `bin/jabswitch -enable` 的配置方法，启用后需要重启目标 Java 应用。本次代码与测试均没有执行该配置命令。
- 高权限应用或不公开任何文本接口的终端、自绘界面仍可能不可读，不承诺任意输入框通用。可使用现有截图翻译；软件不会通过自动复制按键来绕过不可读选区。

官方依据：[JetBrains 无障碍设置](https://www.jetbrains.com/help/idea/accessibility.html)、[Oracle Java Access Bridge API](https://docs.oracle.com/en/java/javase/17/access/java-access-bridge-api.html)、[启用 Java Access Bridge](https://docs.oracle.com/en/java/javase/17/access/enabling-and-testing-java-access-bridge.html)。原生接口名和结构布局另与 OpenJDK 的 AccessBridgeCalls.h、AccessBridgePackages.h 及 WinAccessBridge 导出表核对；没有复制或打包 OpenJDK 实现。

## 验证与边界

只运行模拟焦点、合成 UIA 树、假的 Java 对象/原生后端、假的翻译与复制器，以及已有临时文件测试；没有控制用户桌面，没有启动终端、IDEA 或产品窗口，没有连接真实 UIA/Java 提供者，没有安装钩子或读写系统剪贴板，没有调用真实翻译接口。静态 ABI 测试检查 ContextInfo=6188 字节、SelectionInfo=2056 字节及关键字段偏移，不加载 DLL。

另只读取进程的可执行文件路径和安装文件，确认本机 IDEA 2025.1.1.1 的 jbr/bin 包含 WindowsAccessBridge-64.dll；静态 PE 检查确认其为 x64 且提供所需 10 个导出。该检查没有加载或执行 DLL，也未检查/修改个人 IDE 配置，不能证明 JAB 已启用或真实桌面场景已经成功。

`./scripts/test.ps1 -HeadlessOnly` 全套通过后，针对最后三个先复现失败的容器遍历边界做了修复，补充空容器边界回归，并复跑受影响的 Selection 与 App 项目。最终覆盖 **682 项通过、0 失败、0 跳过**：Core 346、Selection 139、Screenshot/App 120、Update 77。可读取外部真实密钥的可选测试已在执行前显式排除。App 与独立候选服务均集成编译通过，脚本语法和差异空白检查通过；真实终端、IDEA、Java IPC、混合 DPI 和管理员权限场景尚未实测。

微信反馈与手动复制兼容加入后，新增跨进程预览/IPC、复制手势与按键释放、序号/来源/密码/文本校验、取消、无响应提供者独立捕获、模式暂停恢复、展示和共享缓存回归；另补充复制键松开事件丢失后的过期恢复，以及所有者窗口销毁后的自身写入排除。最终 `./scripts/test.ps1 -HeadlessOnly` 一次完整执行 **735 项通过、0 失败、0 跳过**：Core 352、Selection 183、Screenshot/App 123、Update 77。所有新增测试文件均纳入静态审查清单。App 和候选服务集成编译通过，没有打开或测试真实微信、终端、IDEA、系统剪贴板、键盘钩子、真实翻译接口或安装/更新流程。真实场景兼容性尚未验证，计数不代表已经覆盖所有软件。
