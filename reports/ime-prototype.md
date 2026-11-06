# 微软拼音原型记录

日期：2026-11-05。环境：Windows 11 家庭中文版 x64，build 26200；用户语言列表包含简体中文输入法。候选验证在新打开的无标题记事本中进行，未使用在线翻译。

## 实际观察

1. 独立记事本获得焦点后，通过真实键盘事件输入 `s`。屏幕出现微软拼音候选栏：第一可见项为“是”，其后可见“说、上、三、时、四、十”；候选栏出现在编辑位置下方。这是屏幕目视观察，不是对 UIA 读取成功的证明。
2. 桌面自动化工具对该记事本返回的 accessibility 为 `null`；其普通窗口列表没有列出候选栏。工具返回为空不等于证明 Windows UIA 接口一定不可访问，因此不能据此声称 UIA 已验证或不可行。没有取得可供程序稳定使用的候选文字、页信息和屏幕坐标。
3. 已请求 Esc 取消独立样例的组合；随后画面抓取返回 `no screenshot targets found`，因此该次取消的最终画面未能复核。没有在此样例执行 Tab 接受或自动输入。
4. 测试启动期间，窗口枚举延迟导致首次按键短暂落在原有 CSV 记事本标签，出现未提交组合；立即按 Esc 后可见字符计数回到 0，未执行保存或提交。其后重新枚举并激活独立样例继续测试。

## 初次观察时的结论（后续结果见下文）

- 当时尚未验证首个候选 UIA 读取和翻页；下文补充了读取、翻页及 WPF 样例结果。浮窗焦点、安全组合串替换、DPI 和多屏仍无完整实测证据。
- 不知道原应用中组合串的可靠生命周期和精确写入范围，也没有证明安全终止组合后再输入不会损坏内容。因此当前实现不挂接全局 Tab、不注入文字；普通 Tab 透传。它是受限版本，不是输入翻译验收通过。
- 后续需在目标系统使用 UIA3 检查候选原生树和真实页面变化，验证焦点、组合会话和选区快照；再选择可证明安全的组合终止与写入机制。不可用 Escape 后猜测退格数替代此验证。
- `InputSession` 仅提供不可变快照和递增版本，任何事件发生时均需调用方明确失效；它不监测真实输入，也不能单独保证写入安全。

## 补充实验：FlaUI.UIA3 实际候选树

再次确认独立无标题记事本并聚焦编辑区，真实微软拼音中输入 `s`；以下结果来自 `src/PinyinTranslate.Input/ImeProbe` 的 UIA3 只读诊断，并非模拟候选。坐标为当时屏幕物理像素，不能用于其他布局：

```text
Window  Windows 输入体验  class=Windows.UI.Core.CoreWindow  bounds=(168,249,599,72)
  Menu  Microsoft 候选项 UI  AutomationId=IME_Candidate_Window  bounds=(178,267,579,36)
    List  候选项面板  AutomationId=TEMPLATE_PART_CandidatePanel
      ListItem 是 (183,272,62,26), 说, 上, 三, 时, 四, 十
      Button 上一页, 下一页, 表情符号和符号, 搜索建议
      Pane 三项教学提示 (IsOffscreen=true)
```

依据可见、非空、有非零矩形的 `ListItem` 筛选能排除本例按钮和隐藏 Pane；此结论仅针对该实测树。真实 PageDown 后树中七个候选依次为“谁、啥、所、撒、色、水、使”，截图与 UIA 树一致。按 Down 使第二项高亮后，树首项仍是“谁”，与要求的“当前页第一个可见候选”一致。Esc 后截图显示无标题记事本为 0 字符。原有用户文档未用于这次重测。

坐标点 `FromPoint(211,285)` 返回首个 ListItem 并可沿 UIA 树回溯。UIA3 从桌面按 `IME_Candidate_Window` 精确查询也返回一个匹配，但整桌面遍历约需 4.4 秒，不适合高频循环。`CandidateObserver` 是实验性事件驱动只读观察器，不能保证所有环境或输入法配置都能发现候选；下节记录了针对 WPF fixture 的有限事件测试。它不拦截 Tab、不提交或终止组合串，也不提供安全写入证明。页面/焦点变动与订阅事件有竞态时必须放弃结果。

## 补充实验：事件观察器，2026-11-05

本次只使用本地自建 `DesktopFixture` 独立进程及 `CandidateWatch`，未调用翻译 API。WPF 文本框初值为 `If the temperature does not exceed 20 °C, store the samples for 48 hours.`；实际 `FocusedElement` 为 `FixtureText`。控制台仅输出样例进程候选。使用的三条有依据的观察路径：

1. `FlaUI.UIA3.FromPoint` 读取微软拼音 UIA 树：上一节已实测命中真实 `IME_Candidate_Window` 和当前页；它需要事先知道屏幕点，不能作为全局跟随方案。
2. 按 AutomationId 全桌面查询：上一节命中真实候选，但单次约 4.4 秒，不作为高频路径。最初全桌面属性事件监听在 60 秒内有超过 200 次失效，防抖持续被延长，未输出候选，因而放弃此订阅方案。
3. 仅监听桌面顶层结构和焦点，加上 TextInputHost 的 CoreWindow 范围内属性/结构事件，并使用固定合并窗口：新版实际报告 1 个输入法宿主。中文组合 `s` 后输出版本 5、首项“是”，页面 `是、说、上、三、时、四、十`；PageDown 后版本 34 输出首项“谁”，页面 `谁、啥、所、撒、色、水、使`；Down 高亮第二项后版本 35 首项仍为“谁”。翻页带来约 29 个事件，但观察没有无限饥饿。两个页面均曾重复输出同一版本，现增加版本去重；该修复仅编译，尚未复测。

Esc 后合成 WPF 文本框的 UIA DocumentText 与操作前完全相同，未提交中文。Esc 只证明这个单次人工取消保留了该样本文本，不证明跨进程程序性结束组合后能安全插入英文。观察器输出菜单矩形 `(11,19,579,36)`，但该菜单不在 fixture 的窗口截图中，尚未独立验证屏幕坐标或浮窗定位。中文候选的可见截图为前述记事本实验；本次 fixture 截图未包含候选弹窗，不能作为新截图证据。结束实验后释放了桌面。

### 仅预览接入结论

当前只能作为开发诊断，不能安全接入自动翻译预览：候选菜单和目标组合会话的归属未被证明，UIA 事件可能遗漏或迟到，快照缺少组合会话和光标/选区身份，不能确保发送的数据对应用户当前输入。虽然观察器尝试核对焦点/前台根窗口并拒绝受保护祖先，密码控件、属性不支持及切窗竞态尚未在真实桌面做完验证，因此不能用这次实测宣称隐私保护完整。没有实现自动 Tab 拦截、程序性组合取消或文本注入；普通 Tab 保持透传。

## 组合范围与取消实验：2026-11-05

在独立 `DesktopFixture` WPF TextBox、真实微软拼音和同一 Windows 用户下，使用 `src/PinyinTranslate.Input/CompositionProbe` 的 UIA3 COM 只读探针。该探针只读取前台焦点为该 fixture 的 `FixtureText`，未触及用户文档。这里测试两个有依据的只读接口路径：TextPattern2 的 `GetCaretRange` 和 TextEditPattern 的 `GetActiveComposition`；后者才是活动组合串范围接口。辅助使用基础 TextPattern 的 DocumentRange 与 GetSelection。结果：

| 状态 | UIA 文档内容 | 基础 TextPattern 选区（UTF-16） | 高级接口 |
| --- | --- | --- | --- |
| 输入前 | 固定英文样例，73 个 UTF-16 单元 | `[73,73]` | TextPattern2 不可用；TextEditPattern 不可用 |
| 输入拼音 `s`、候选为“是”期间 | 固定英文样例后出现 `s` | `[74,74]` | TextPattern2 不可用；TextEditPattern 不可用 |
| 人工按 Esc 后 | 恢复为完全相同的 73 单元英文样例 | `[73,73]` | TextPattern2 不可用；TextEditPattern 不可用 |

截图观察到真实微软拼音候选与临时 `s`；读取是在保留真实组合状态时进行。该结果表明此控件的 UIA 基础文本会包含未提交的组合文字，不能仅根据该文本或前后长度把 `s` 当成已提交内容，也不能从基础选区证明活动组合范围或判定会话身份。人工 Esc 在这一次试验中恢复了原文和光标，但不能推广为其他控件或竞争条件下的安全机制。按要求发现活动组合接口不可用后停止实验，没有进行任何程序性删除、英文替换或自动 Tab 注入；Windows 普通 Tab 仍透传。发布前仍需独立证明会话、精确取消、范围与焦点条件。

微软官方资料与路线边界：

- [TextPattern2.GetCaretRange](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextpattern2-getcaretrange) 提供光标处零长度范围；[TextEditPattern.GetActiveComposition](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtexteditpattern-getactivecomposition) 才是活动组合范围。目标控件不公开这两个接口时不能假定它们可用。
- [微软 IMM 多线程约束](https://learn.microsoft.com/en-us/windows/win32/intl/developing-ime-aware-multiple-thread-applications) 说明 IMM 对上下文或窗口句柄进行线程归属检查，建议线程不要访问其他线程创建的输入上下文。因此不能假定独立进程能用 IMM 读取、取消或修改目标线程组合；本实验没有尝试绕过该边界。
- [TSF UILess 文档](https://learn.microsoft.com/en-us/windows/win32/tsf/uiless-mode-overview) 描述应用将 `ITfUIElementSink` 注册到其线程管理器的方式。这并不证明独立进程创建的 sink 能全局订阅别的应用的输入会话；本实验未验证全局 TSF 监听。

## 本轮只读原型修正：2026-11-05

按用户当前“暂不测试”的要求，本轮只进行了代码审查与编译，没有启动桌面样例、运行自动化测试、调用翻译 API 或发送候选文本。以下是实现变更，不是实测结论：

- 候选观察器要求调用方明确提供目标进程和焦点控件 AutomationId，核对持有的进程句柄、前台进程及焦点；根窗口句柄为零时拒绝读取。诊断工具只接受本项目 fixture 构建目录中的 `DesktopFixture.exe` 和 `FixtureText` 控件。
- 对不支持的可见性或矩形属性、非正尺寸和候选项不完全位于面板内的情况拒绝观察。当前只接受可确认共同纵向交集、UIA 顺序与从左到右不重叠的单行候选；这可能拒绝其他合法布局，且不证明屏幕可见或候选会话归属。
- 加入初始化就绪状态、观察版本和启停代次、单槽异步二次复核及超时失效；禁用后在 UIA 工作线程释放宿主订阅。对持续变化的事件等待稳定并显示状态，不因等待过久而强制发布。
- 诊断工具只输出版本、进程和矩形等元数据，不输出候选内容或内容分类；目标样例进程退出后结束。会话状态机对缺失组合、焦点、候选页和光标/选区标识的快照拒绝建立票据。

这些条件不能证明 UIA 事件完整、候选菜单与当前组合属于同一会话、候选矩形可用于多屏定位，或组合串可以跨进程安全结束。严格过滤也可能使实际候选完全不显示。自动候选上传、翻译预览、Tab 拦截与输入仍未接入发行软件，普通 Tab 保持原行为。后续需要先在独立样例及多个真实目标中验证会话归属、焦点与密码边界、事件遗漏/迟到、坐标和组合串安全提交，再决定是否启用。

相关平台边界见微软的 [UIA 事件说明](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-eventsforclients)、[UIA 线程要求](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading) 与 [TSF 组合上下文所有者接口](https://learn.microsoft.com/en-us/windows/win32/api/msctf/nn-msctf-itfcontextownercompositionservices)。
