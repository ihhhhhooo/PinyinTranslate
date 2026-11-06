# PinyinTranslate

**简体中文** | [English](README.en.md)

保留微软拼音的 Windows AI 翻译工具，支持 **输入翻译、划词互译和截图对照**。

**[下载最新版](https://github.com/ihhhhhooo/PinyinTranslate/releases/latest)** · [使用演示](#使用演示) · [更新记录](RELEASE-NOTES.md)

适用于 **Windows 11 x64**，需要自己的 **DeepSeek API Key**，无需另装 .NET。

## 功能

| 功能 | 默认快捷键 | 怎么用 |
| --- | --- | --- |
| 输入翻译 | `Ctrl+Alt+T` | 开启后自动翻译微软拼音首候选并复制；看到“已复制”后，按 `Esc` 取消拼音，再 `Ctrl+V` 粘贴。 |
| 划词互译 | `Ctrl+Alt+D` | 开启后选中文字互译；读不到时，选中后按 `Ctrl+C` 兼容翻译，终端也可用 `Ctrl+Shift+C`。 |
| 截图翻译 | `Ctrl+Alt+S` | 框选区域，查看原图与逐段原文、译文对照。 |

支持中文（简体／繁体）、英语、日语、韩语、法语、德语、西班牙语、葡萄牙语、俄语、越南语。输入与截图可分别设置目标语言，划词可任选两种语言互译。

另有软件内更新、用量统计和多种界面主题。

## 使用演示

<!-- 视频预留位：上传后，将下方占位图替换为视频链接。 -->
![使用视频预留位，待上传](docs/media/demo-video-placeholder.svg)

<!-- 截图预留位：将下方各占位图替换为对应的真实截图。 -->
| 输入翻译 | 划词互译 | 截图对照 |
| --- | --- | --- |
| ![输入翻译截图，待上传](docs/media/input-placeholder.svg) | ![划词翻译截图，待上传](docs/media/selection-placeholder.svg) | ![截图翻译截图，待上传](docs/media/screenshot-placeholder.svg) |

演示素材待补充，替换方法见 [图片与视频说明](docs/media/README.md)。

## 快速开始

1. 从 [Releases](https://github.com/ihhhhhooo/PinyinTranslate/releases/latest) 下载 `Setup.exe` 安装；也可选择 `Portable.zip` 解压使用。
2. 打开“设置”，填入自己的 DeepSeek API Key 并保存。
3. 在主页各功能的快捷键旁选择语言，点击“保存设置”，再开启所需功能或使用上方快捷键。

**以后升级：** 设置 → 软件更新 → 检查更新 → 下载更新 → 更新并重启。

## 使用说明

- 翻译内容会发送给 DeepSeek，API 费用由自己的账户承担；软件内费用统计为估算。
- 输入翻译会覆盖剪贴板。划词模式开启时，选中后手动复制的新文字也会用于翻译；关闭模式后停止。
- 部分应用的候选或选区无法读取时，可用复制兼容或截图翻译；无法保证所有软件都支持自动读取。

[详细使用与开发说明](docs/guide.md) · [多语种说明](reports/multilingual.md) · [费用说明](reports/pricing.md) · [更新说明](reports/updates.md) · [反馈问题](https://github.com/ihhhhhooo/PinyinTranslate/issues) · [MIT License](LICENSE)
