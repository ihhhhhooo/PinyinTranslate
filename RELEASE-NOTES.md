# PinyinTranslate 1.0.1

- 新增 `PinyinTranslate.Desktop-win-x64-Setup.exe` 安装版：下载后双击安装并启动，无须手动解压；以后从桌面或开始菜单快捷方式打开。
- 安装版和便携 ZIP 版都支持“设置 → 软件更新”，沿用原来的本机密钥、设置和用量统计。
- 已开启的本软件自启动项会跟随迁移到新版本的启动器；不会自动开启自启动。

先退出正在运行的旧版，再运行 `Setup.exe`。程序默认安装在当前 Windows 账户的 `%LOCALAPPDATA%\PinyinTranslate.Desktop`，翻译数据仍保存在 `%LOCALAPPDATA%\PinyinTranslate`。安装版需要首次安装一次，之后日常使用和软件内更新均不需要手动解压。

`Portable.zip` 仍可选用，解压后运行根目录的 `PinyinTranslate.exe`。不要将 `.nupkg` 当作安装程序。

输入翻译的操作不变：等英文显示“已复制”后，按 Esc 取消拼音，再按 Ctrl+V 粘贴。
