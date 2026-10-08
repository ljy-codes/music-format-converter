# 第三方组件说明

根目录 MIT LICENSE 仅用于本项目自有代码，不重新许可第三方组件。

桌面发布包含 .NET 自包含运行时、Avalonia 及其图形/字体依赖（包括 SkiaSharp、HarfBuzzSharp、MicroCom、ANGLE 等）；其自身许可及 notices 以对应锁定版本的上游包为准。开发/测试还使用 xUnit、Microsoft.NET.Test.Sdk 和 Avalonia.Headless.XUnit。

应用通过独立进程调用 FFmpeg/ffprobe。Windows 与 Mac 引擎配置和许可不同，见 `docs/engine-provenance.md`。安装包 `tools/licenses`、`tools/LICENSE.txt`、`tools/provenance` 保存引擎随附许可、对应 FFmpeg 源码、构建来源和配置。

Mac 0.1.1 的包许可声明、原有版权元数据及包内 license/notices 由构建脚本从实际还原的 NuGet 包复制到 `Contents/Resources/third-party`。标准许可文本取自固定 SPDX license-list-data v3.27.0。FFmpeg 与 LAME 均以可替换共享库分发，并附对应源码、构建参数、配置和原始许可。Windows BtbN 引擎仍需补齐其全部第三方依赖对应源码，Windows 二进制不随本次 Mac Release 发布。
