# 音乐格式转换器 0.1.1：构建与离线打包

## 范围与发布契约

- App：`src/MusicFormatConverter.App/MusicFormatConverter.App.csproj`，可执行名 `MusicFormatConverter.App`。
- 固定 .NET SDK **10.0.400**，Avalonia **11.3.22**；版本从 `Directory.Build.props` 读取。
- Windows x64：自包含发布，真实 Inno Setup 安装 EXE，可选便携 ZIP。
- Mac：分别在原生 arm64 / x64 macOS 构建机编译对应 `.app`、`.dmg`；打包脚本要求全量测试、挂载包验证、原生启动和六模式转换均通过。
- 程序运行时不下载引擎，不依赖系统 FFmpeg 或系统 .NET。
- 发布目录为 `MusicFormatConverter.App[.exe]` 同级 **`tools/`**，不是 `tools/win-x64`。开发引擎位于仓库 `tools/<RID>/`。Core 可使用 `MFC_ENGINE_DIR` 覆盖。
- 安装包不包含 `ffplay`，但完整保留 FFmpeg/ffprobe 所需共享库、许可、来源归档和哈希清单。
- 0.1.1 为 Mac 引擎加入锁定源码编译的共享 LAME 3.100，补齐 MP3 编码；原有六模式、默认 Apple Music 智能保真及不降低采样率/声道的策略不变。

所有下列命令均在仓库根目录执行。Windows PowerShell 要求 **PowerShell 7+**，不是 Windows PowerShell 5.1。

## Windows：先获取、再离线打包

```powershell
pwsh -NoProfile -File ./scripts/fetch-engine.ps1 -Proxy http://127.0.0.1:7897
pwsh -NoProfile -File ./scripts/test-engine.ps1
$env:MFC_ENGINE_DIR = (Resolve-Path ./tools/win-x64).Path
dotnet test ./tests/MusicFormatConverter.Core.Tests -c Release
pwsh -NoProfile -File ./scripts/package-windows.ps1 -Zip
```

没有代理的环境省略 `-Proxy`。**不修改系统、Git、NuGet 或全局代理**。下载使用固定 release，而不是 `latest`；本地有效缓存直接复用。缓存哈希不符就失败，不自动覆盖可疑文件。

本机 Inno Setup 默认位置：

```text
%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe
```

要求 Inno Setup **6.7+**（本机验证为 6.7.3），用于原生深色安装/卸载向导。其他机器通过 `-IsccPath '...\ISCC.exe'` 指定。脚本不安装 Inno，不执行生成的安装包；CI runner 也需要兼容版本，过旧编译器应报错而不是回退到浅色包。

输出：

```text
artifacts/installer/MusicFormatConverter-0.1.1-win-x64-setup.exe
artifacts/installer/MusicFormatConverter-0.1.1-win-x64-setup.exe.sha256
artifacts/MusicFormatConverter-0.1.1-win-x64.zip          # -Zip 时
artifacts/MusicFormatConverter-0.1.1-win-x64.zip.sha256   # -Zip 时
artifacts/build/windows-<GUID>/app/                     # 实际 self-contained 输出
```

每次使用新构建目录，避免把旧 DLL 打进去。安装包按当前用户安装，不提权、不设开机启动、不注册文件关联、不自动启动 App；卸载不清理用户音源、转换输出和报告。安装包尚未代码签名。安装与卸载共用深蓝黑 Polar 主题、青紫色图标，主要操作流程中文化（未覆盖的系统错误保留英文）。系统高对比度模式保留 Inno 的无障碍回退。

Windows EXE、窗口、快捷方式、已安装应用列表使用同源多尺寸 ICO；Mac bundle 使用 ICNS。原始几何图标及侧图生成器为 `scripts/render-branding.py`，生成资源随源码保存，不增加用户运行依赖。AppId 与安装路径保持不变。

`package-windows.ps1` **不会自动获取 FFmpeg**。缺引擎、许可证、对应源码/构建配方、清单、任何共享 DLL、哈希不符或实际转换失败均终止。它先运行版本与 FLAC→ALAC→PCM 一致性测试，再发布 App，复制清单中的全部文件，再对发布副本复测。NuGet/self-contained runtime 的首次 `dotnet publish` 仍可能联网恢复构建依赖；这与成品运行时离线不同。

## Mac：原生源码引擎、原生 App 与真实 DMG

前提：macOS 14+、对应 CPU 的 .NET SDK 10.0.400、Python 3.11+、Xcode Command Line Tools。不从 Homebrew 或其他移动版本源下载安装 FFmpeg。Intel 不要求 NASM，使用 `--disable-x86asm`，性能可能低于专门优化构建。

```bash
# Apple Silicon，必须在原生 arm64 终端（不是 Rosetta）
bash scripts/build-engine-macos.sh osx-arm64
MFC_ENGINE_DIR="$PWD/tools/osx-arm64" dotnet test tests/MusicFormatConverter.Core.Tests -c Release
bash scripts/package-macos.sh osx-arm64

# Intel Mac 使用同一流程，将 RID 改为 osx-x64
```

引擎脚本下载锁定的官方 FFmpeg 8.1.3 tarball，验证 SHA256 后本地编译；保持内建解码器、封装器、过滤器和音频编码器，不启用 GPL、nonfree、version3、network 或外部库自动探测；明确启用源码构建的 LAME 与 macOS 系统 zlib（PNG 封面所需）。保留 lavfi，执行真实音频生成、FLAC→ALAC 无损 PCM 对比和 AAC 编码测试。

使用共享 dylib；将所有 FFmpeg dylib 放在引擎旁，以 `@loader_path` 寻址。验证架构、`otool -L` 依赖解析；只允许同目录引擎库和 Apple 系统库，不允许 Homebrew/构建机绝对路径。仅显式启用锁定的 libmp3lame 共享编码器；LAME dylib、源码、配置及许可一起分发。没有 libopus 外部编码器；内建 MP3/Opus 解码器保留。

默认产物：

```text
artifacts/build/package-osx-<arch>.<random>/音乐格式转换器.app
artifacts/installer/MusicFormatConverter-0.1.1-osx-<arch>-unsigned.dmg
artifacts/installer/MusicFormatConverter-0.1.1-osx-<arch>-unsigned.dmg.sha256
```

`unsigned` 表示**没有 Developer ID 签名/公证**，脚本仍作 ad-hoc 签名以支持本机 Mach-O 加载。生成 `.app` 不是伪造文件夹：它包含自包含 native apphost、runtime、Info.plist 和完整引擎；DMG 使用 Mac 系统 `hdiutil create/verify`。这不代替 UI 启动与音频导入 Apple Music 实机验收。

引擎目录已存在时，重新编译不会默默合并覆盖；新引擎在日志指定的暂存目录，需用户明确移开旧目录再操作。包脚本始终要求已经验证的引擎，不会替代用户下载。

### 可选 Developer ID 签名 / 公证（不在默认 CI 启用）

```bash
# 前提：证书及私钥已由用户安全导入本机 keychain。
export MFC_CODESIGN_IDENTITY='Developer ID Application: Your Name (TEAMID)'
bash scripts/package-macos.sh osx-arm64

# 只有明确授权上传 Apple 公证服务后，才设置以下变量。
# keychain profile 由用户通过 xcrun notarytool store-credentials 预先创建。
export MFC_NOTARY_PROFILE='mfc-notary'
bash scripts/package-macos.sh osx-arm64
```

脚本由内到外签名 Mach-O，重新计算签名后引擎哈希，再签 App；可选提交 Apple 公证并 staple，DMG 也验证/公证。默认不会上传 Apple，不执行 GitHub Release 发布。需实机验证 Developer ID、hardened runtime 和 .NET JIT；不得把脚本能力写成已经公证通过。请勿全局关闭 Gatekeeper。

## CI 与核验

`.github/workflows/package.yml` 仅 `workflow_dispatch`，只读仓库权限。Windows 构建实际 EXE/ZIP；`macos-15` 原生 arm64、`macos-15-intel` 原生 x64 分别编译引擎，执行引擎测试和 Core 测试，再生成明确标注 unsigned 的 DMG。架构不匹配直接失败，不使用跨编译伪装实测。

工作流只上传 Actions 构建产物供下载，**没有 release/publish 步骤**；本地脚本不上传 GitHub、不 commit、不 push。CI runner 标签和工具可用性属于外部条件，如标签被平台调整，需要维护 matrix，不能把跳过的 Mac job 视为成功验收。

```powershell
# 仅检查自有脚本，以及缺许可/哈希篡改/缺 DLL 的拒绝行为
pwsh -NoProfile -File ./scripts/test-packaging.ps1
```

## 已验证与待验收

- 2026-10-08 Windows：已下载并核验锁定 ZIP、上游 checksum、源码归档及构建配方；两个工具实际运行；FLAC→ALAC PCM 哈希一致。
- 2026-10-08：PowerShell、Bash、Python、JSON、plist/YAML 语法检查通过；缺许可、许可哈希篡改、缺 avcodec DLL、清单越界路径四项负向测试均正确拒绝；固定下载脚本再次执行成功（复用核验后的缓存）。发布清单共 20 个文件，其中 7 个 DLL，0 个 ffplay。
- 上述语法检查不是 Mac 运行测试。
- Windows 完整 App 安装包生成、UI 启动和主代理全量业务测试，以本次运行日志及主代理验收为准。
- Mac 发布的逐架构结果见 Release 的 `validation-osx-*.json` 与 Actions 日志；Developer ID 签名、公证、Apple Music 实机兼容仍需另行验证。
- Windows BtbN 引擎含较多第三方库；公开分发前的依赖许可/完整对应源代码核对见 `engine-provenance.md`，不能仅凭 LGPL 文件认为法务已完成。

## Mac Release

手动触发现有 `.github/workflows/package.yml`：macos-15 (arm64)、macos-15-intel (x64) 各自编译 FFmpeg/LAME，运行整套 Core/App/CLI 测试，构建自包含 DMG，再实际挂载并复制到独立临时目录。验证所有 Mach-O 架构、签名、引擎哈希/动态依赖、六种模式的编码/采样率/声道和无损 PCM，确认原生界面进程成功启动。

只有两架构都通过后，才创建同一版本的 GitHub Release，上传两份 DMG 与 SHA256。逐架构验证 JSON 由本地/构建目录保存并追加到 Release。先上传 draft 再公开；已有 Release 不覆盖，必须修改版本号。普通 package.yml 继续只上传 Actions 产物。`docs/macos-release-workflow.yml` 是可选的自动发布工作流示例，启用它需要 GitHub workflow 权限；本次使用现有工作流构建后发布，不依赖新增权限。

安装包在 `Contents/Resources/third-party` 保存 NuGet 包的许可声明、版权元数据及随包 license/notices。FFmpeg/LAME 对应完整源码和构建参数在 `Contents/MacOS/tools/provenance`；这两个共享工具库可被用户替换。

### Mac bundle 布局与签名

主程序与原生辅助可执行文件留在 `Contents/MacOS`；托管 DLL、JSON 和其他数据位于 `Contents/Resources/managed`，动态库位于 `Contents/Frameworks`，引擎与其源码/许可位于 `Contents/Resources/tools`。`MacOS` 中使用 bundle 内的相对符号链接保持 .NET 和引擎查找路径。签名按实际 Mach-O 文件逐个完成，最后签主应用；不将托管 DLL 当作原生代码，不在签名完成后改动资源。

布局依据 Avalonia 官方 Mac 部署说明：https://docs.avaloniaui.net/docs/deployment/macos 。打包检查遍历整个 Contents，核对所有原生二进制架构，并验证复制后的完整 bundle 签名和真实启动。
