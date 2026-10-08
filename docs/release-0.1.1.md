# 音乐格式转换器 0.1.1 · macOS Intel / Apple Silicon

macOS 14 或更新版本，下载对应芯片的 DMG：

- **Apple Silicon（M1/M2/M3/M4/M5 等）**：`MusicFormatConverter-0.1.1-osx-arm64-unsigned.dmg`
- **Intel**：`MusicFormatConverter-0.1.1-osx-x64-unsigned.dmg`

打开 DMG，将“音乐格式转换器.app”拖入 Applications，然后从“应用程序”打开。安装包内置 .NET 运行库、FFmpeg 与 LAME，无需另装工具，转换时无需联网。

支持中文界面、文件/文件夹与拖放导入、预检、智能保真/ALAC/AAC/MP3/FLAC/WAV 六种模式、取消、失败重试及 JSON/CSV 报告。新增 Mac 的 MP3 320 kbps 编码支持；原文件不改写，已有输出不覆盖。

两种架构分别在原生 macOS 构建机编译和测试。发布前执行全套自动化测试、音频 PCM 校验、DMG 挂载与复制验证、原生应用启动及六模式真实转换；逐架构 `validation-*.json` 与 SHA256 附件提供验证记录。

这些包使用 ad-hoc 签名，**未使用 Apple Developer ID 签名或公证**。macOS 可能阻止首次打开；确认来源后，可在“系统设置 → 隐私与安全性”选择“仍要打开”。不要关闭系统整体安全检查。Apple Music 的实际导入播放仍需在用户设备上验证。

FFmpeg、LAME 的锁定源码、构建参数及许可随应用保存在 `Contents/MacOS/tools/`；.NET/Avalonia/原生图形组件的包许可与 notices 位于 `Contents/Resources/third-party/`。项目源码通过本 Release 的 Source code 下载。Windows 原有源码预发布保留，本次附件仅发布 Mac 安装包。
