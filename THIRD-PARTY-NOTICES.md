# 第三方组件说明

根目录 MIT LICENSE 仅用于本项目自有代码，不重新许可第三方组件。

桌面发布包含 .NET 自包含运行时、Avalonia 及其图形/字体依赖（包括 SkiaSharp、HarfBuzzSharp、MicroCom、ANGLE 等）；其自身许可及 notices 以对应锁定版本的上游包为准。开发/测试还使用 xUnit、Microsoft.NET.Test.Sdk 和 Avalonia.Headless.XUnit。

应用通过独立进程调用 FFmpeg/ffprobe。Windows 与 Mac 引擎配置和许可不同，见 `docs/engine-provenance.md`。安装包 `tools/licenses`、`tools/LICENSE.txt`、`tools/provenance` 保存引擎随附许可、对应 FFmpeg 源码、构建来源和配置。

**本文件不是完整的第三方许可清单。** 首版为本地试用/验收产物，尚需整理并审核所有随包运行库、图形原生库和 Windows FFmpeg 第三方依赖的对应 notices 与源码提供义务后，才能评估公开二进制分发。不将存在 LICENSE 文件或能打包成功表述为分发审核已完成。
