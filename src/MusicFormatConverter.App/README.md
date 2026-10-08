# 音乐格式转换器 · Avalonia App

本目录仅包含桌面 UI 与任务调度，不实现音频策略，不改写源文件。目标为 `.NET 10`，Avalonia 包固定 `11.3.22`。主窗口 1280×820，最小 1050×720；保留系统原生标题栏，支持深墨蓝和浅色主题。左侧使用 `ListBox + VirtualizingStackPanel`，右侧设置宽度固定 320 DIP，小窗口内设置可滚动。

## 调用边界

`MainWindow` → `WorkspaceViewModel` → `IConversionService` → `CoreConversionService` → Core。

- `CoreConversionService` 用后台任务调用公开服务，避免同步扫描准备阻塞界面；不修改 Core 契约。
- 模型直接使用 Core records，包括未来新增的 init 属性，不重新构造已预检的 Plan。
- UI 负责路径导入、原生多选文件/文件夹、拖放、输出位置、设置快照、状态与取消。
- Core 负责音频识别、源文件变化检测、路径/空间安全、转换、完整性校验、临时文件与子进程回收。
- 报告使用原生文件夹选择器，调用 `ReportWriter.WriteAsync`，展示返回路径。
- 只有用户点击“打开输出”时才通过 `UseShellExecute` 打开经过 `GetFullPath + Directory.Exists` 检查的文件夹。不打开音频、元数据链接、报告链接或任意命令。
- 不涉及数据库、Redis、MQ；界面不负责安装或远程发布。

## 状态与安全约束

| 状态 | 允许操作 | 限制 |
|---|---|---|
| 空队列 | 导入、设置、切换主题 | 无预检/转换/导出 |
| 待预检 | 导入、清空、设置、预检 | 必须有完整输出路径；不可直接转换 |
| 预检中 | 取消、切换主题 | 锁定来源/设置/重复预检 |
| 预检完成 | 查看策略/不支持原因、手动开始转换 | 预检绝不自动转换；全部不支持时禁用转换 |
| 转换中 | 查看逐项/批次进度、取消 | 锁定导入、清空、设置、报告与重复执行 |
| 正在取消 | 等待 | 不提前回到可编辑状态，不遗弃正在运行的任务 |
| 批次结束 | 查看成功/失败/跳过/取消统计、报告、打开输出、仅重试失败项 | 整批再次转换必须重新预检；已跳过项不计失败、不参与重试 |
| 导出中 | 取消 | 导出失败保留结果，可再次导出 |

任一转换设置或新增输入都会废弃计划、旧结果与重试资格；重复导入相同路径不作无意义失效。设置在忙碌时由控件和 ViewModel 双重拦截。预检后的整批计划只消费一次。

默认智能保真、保留目录、完整音频检查、转码时保留标签/封面。元数据选项仅影响重封装/转码：原样复制始终保留标签与封面，取消勾选不代表移除 Copy 文件的元数据。关闭检查立即显示风险说明。歌词、加密文件处理、播放器自动导入均不承诺。预检 warnings 原样可展开查看，包括 Core 提供的 Apple Music 实测边界。

窗口关闭（含应用退出请求）会先取消，再等待当前 operation 完成才真正关闭。Core 的取消结果完整写回队列。进度回调通过 UI dispatcher 和任务代次校验，旧任务/取消后的迟到消息不会污染新队列。异常显示简短消息，不从 UI 事件向外抛出。

## 构建与运行

在仓库目录运行：

```powershell
dotnet build src/MusicFormatConverter.App/MusicFormatConverter.App.csproj -c Release
dotnet run --project src/MusicFormatConverter.App/MusicFormatConverter.App.csproj -c Release
```

引擎由 Core 发现（安装包 `tools` 或开发环境 `MFC_ENGINE_DIR`），UI 不自动下载。缺失引擎会在预检阶段显示错误而不是启动崩溃。

需要 NuGet 代理时，仅为当前 PowerShell 进程设置：

```powershell
$env:HTTP_PROXY='http://127.0.0.1:7897'
$env:HTTPS_PROXY=$env:HTTP_PROXY
```

## 测试与截图证据（2026-10-08）

初版 UI 验证：Release 构建 **0 警告、0 错误**；当时完整 App 测试 **25/25 通过、0 跳过**。其中 18 项状态测试、7 项无头界面测试（主题/尺寸参数化计入用例数）。已生成并人工查看 5 张 PNG。后续导入列表及跳过状态扩展后，App 测试为 **40/40**；本次完整验收见仓库 `docs/verification.md`。

状态测试先建立接口骨架，首次运行观测到 **8 项预期失败**（预检门禁、设置失效、安全默认值），随后实现并通过。界面截图检查发现最小尺寸空状态末行超出内边距，增加几何断言复现 **2 项失败**后缩紧间距修复。

完整测试：

```powershell
dotnet test tests/MusicFormatConverter.App.Tests/MusicFormatConverter.App.Tests.csproj -c Release
```

Core 尚未实现时，可独立运行不依赖引擎/App 控件的状态测试：

```powershell
dotnet test tests/MusicFormatConverter.App.Tests/MusicFormatConverter.App.Tests.csproj -p:AppStateOnly=true
```

`AppStateOnly` 只用于测试编译链接真实 ViewModel、接口与 Core 模型，没有替换生产 Core。完整模式引用真实 App 并使用固定版本 `Avalonia.Headless.XUnit 11.3.22`。

输出实际 Skia 渲染截图：

```powershell
$env:MFC_UI_SCREENSHOT_DIR=Join-Path $PWD 'tests/MusicFormatConverter.App.Tests/Artifacts'
dotnet test tests/MusicFormatConverter.App.Tests/MusicFormatConverter.App.Tests.csproj -c Release
```

截图包括空态深/浅色的 1280×820、1050×720，以及 1000 项虚拟队列。测试中的队列是隔离的合成夹具，不会出现在生产界面；生产窗口没有假文件或示例数据。

覆盖：预检和转换严格分离、设置/输入失效、去重、异常恢复、逐项进度、迟到回调、取消等待、失败重试、结果保留与报告异常；界面空态和按钮绑定、双主题/尺寸、1000 项实际容器数量小于 50、主题按钮、关闭窗口等待模拟引擎清理。

## 已知验收边界

- Headless 验证真实 Avalonia 控件/布局/Skia 渲染，但不证明系统原生选择器、桌面拖放、系统 shell、macOS 菜单与高 DPI 在各台设备实测通过，仍需 Windows/macOS 真机冒烟测试。
- UI 状态取消测试验证“等待服务清理后再关闭”；实际 FFmpeg 进程树和源文件完整性由 Core 的真实引擎测试验证。
- 文件/路径/结果明细支持选择复制；长队列行省略的原因可选中查看完整内容或悬停查看。最小窗口的右侧设置需要滚动。
- 不持久化队列/主题，不断点恢复。系统强制结束或断电不属于窗口正常关闭保证。
- 报告路径与失败信息只显示，不自动打开外部内容；缺少引擎时窗口仍正常启动。
