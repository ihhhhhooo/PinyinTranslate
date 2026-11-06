# 第三方许可

以下文件和项目的许可随源码提供，或者在构建包时从对应已还原的 NuGet 包复制到发行目录 `licenses`：

- .NET 运行时及 WPF 运行时（10.0.14）：MIT；发行包内附 `licenses/dotnet-runtime-LICENSE.txt`、`licenses/dotnet-windowsdesktop-LICENSE.txt` 和运行时附带的 `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt`。包括 `System.Security.Cryptography.ProtectedData`；参见 [.NET runtime](https://github.com/dotnet/runtime) 和 [WPF](https://github.com/dotnet/wpf)。
- FlaUI.Core / FlaUI.UIA3 5.0.0：MIT，见 `licenses/FlaUI-MIT.txt`。仅用于输入法研究与测试工程；当前桌面发行程序未引用该工程。
- Interop.UIAutomationClient 10.19041.0：MIT，见 `licenses/UIAutomation-Interop-MIT.txt`。它是 FlaUI 的开发依赖，当前桌面发行程序不引用它。
- xUnit.net 2.9.3：Apache-2.0，见 [xUnit 许可](https://github.com/xunit/xunit/blob/main/license.txt)。仅测试依赖，不随程序发行。
- Microsoft.NET.Test.Sdk / VSTest 17.14.1：MIT，见 [VSTest 许可](https://github.com/microsoft/vstest/blob/main/LICENSE)。仅测试依赖，不随程序发行。
- Velopack 1.2.161：MIT，见 `licenses/Velopack-MIT.txt` 和 [固定版本的上游许可](https://github.com/velopack/velopack/blob/1.2.161/LICENSE)。用于便携版更新；当前使用的 .NET 10 托管包没有额外 NuGet 运行时依赖。`licenses/Velopack-THIRD_PARTY_NOTICES.md` 保留同版 vpk 的上游构建工具清单，该清单不代表其中的开发工具随本程序发行。
- coverlet.collector 6.0.4：MIT，见 [coverlet 许可](https://github.com/coverlet-coverage/coverlet/blob/master/LICENSE)。仅测试依赖，不随程序发行。

本版本 WPF、UI Automation 使用 .NET 框架组件，Win32 调用使用手写 P/Invoke。CommunityToolkit.Mvvm 与 CsWin32 尚未接入，不计入发行依赖。自包含发行目录还包含 .NET 运行时的原生及托管组件；其完整上游第三方声明以随包提供的文件为准。
