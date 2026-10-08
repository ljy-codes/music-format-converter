# 固定合成音频测试素材

## 来源与使用声明

本目录两份音频于 **2026-10-08** 在本项目 Windows 开发环境中，使用当前项目的 `tools/win-x64/ffmpeg.exe` 从 `lavfi sine` **自行合成**。

**自制无版权音乐测试素材说明：** 音频只有 997 Hz 的纯正弦测试音，不含任何现成音乐、第三方录音、采样、旋律或人声。可随本项目自由分发并用于测试。中文元数据是虚构测试文本，不是对真实歌曲或艺人的引用。本声明仅描述样本音频内容，不改变 FFmpeg 本身及其组件的许可证。

- 原始测试音：0.5 秒、48,000 Hz、双声道。
- MP3：192 kbps；Opus：128 kbps。
- 生成引擎：`n8.1.3-14-g330caae0c1-20261007`，完整版本/构建配置见 `ffmpeg-version.txt`。
- 两份文件均包含 `title=中文标题`、`artist=测试歌手`、`album_artist=专辑歌手`、`track=2/9`、`disc=1/2`。
- `album_artist` 是 Core 使用的规范化 albumartist 字段。Opus 的标签存放在音频流中，测试验证转为 MP4/ALAC 后的容器标签仍一致。
- Opus 容器时长为 0.5065 秒，包含编解码延迟；保留现有 preskip/时长校验覆盖。

| 文件 | 大小（字节） | SHA-256 |
|---|---:|---|
| `sine-997hz.mp3` | 13392 | `12F4F38A6896BD27812BA5E0B283B551396A21AA09E7CAAA9E894F38F40FF695` |
| `sine-997hz.opus` | 10075 | `770AC07829D9E8617A8D9DD4697A61412A470BB45FC325ECBC3ADDF1CDE00DFA` |

## 为什么固定输入

macOS 引擎采用 FFmpeg 原生、最小外部依赖构建，没有外部 MP3/Opus 编码器。转换器需要**解码**这类来源，并不需要在运行测试时**编码生成**它们。

- MP3 混合批次测试复制固定 MP3 到独立临时目录，明确断言预检为 `Copy`，并比较输出与输入的全部字节。
- 两个 Opus 集成测试使用同一固定 Opus，继续真实执行 Opus → ALAC、完整解码校验以及中文标签映射。
- 测试项目将 `Fixtures/**/*` 复制到测试输出目录；样本从 `AppContext.BaseDirectory/Fixtures` 定位，不依赖工作目录、网络或 Windows 路径。
- 每个测试复制独立输入，不改写共享 fixture。
- 新增两个普通 Theory 用例校验文件存在及 SHA-256。漏复制/缺样本/样本变化均失败，**不通过跳过掩盖问题**。
- C# 测试运行时不再请求外部 MP3/Opus 编码器；其他动态合成场景只用 FFmpeg 原生编码器。

## 可审计的生成命令（仅维护样本时使用）

以下命令需要当前 Windows 引擎提供生成用编码器；**不是测试准备步骤、MSBuild 步骤或 CI 步骤，macOS 无需运行**。固定音频已随源码提供。

在仓库目录运行，`-n` 会拒绝覆盖已存在的样本：

```powershell
$ff = (Resolve-Path 'tools/win-x64/ffmpeg.exe').Path
$fixtures = Join-Path $PWD 'tests/MusicFormatConverter.Core.Tests/Fixtures'
$common = @(
    '-v', 'error', '-nostdin', '-n',
    '-f', 'lavfi', '-i', 'sine=frequency=997:sample_rate=48000:duration=0.5',
    '-ac', '2',
    '-metadata', 'title=中文标题',
    '-metadata', 'artist=测试歌手',
    '-metadata', 'album_artist=专辑歌手',
    '-metadata', 'track=2/9',
    '-metadata', 'disc=1/2'
)
& $ff @common '-c:a' 'libmp3lame' '-b:a' '192k' '-id3v2_version' '3' (Join-Path $fixtures 'sine-997hz.mp3')
& $ff @common '-c:a' 'libopus' '-b:a' '128k' (Join-Path $fixtures 'sine-997hz.opus')
```

重新生成可能因 Ogg 序号、引擎版本等产生不同字节。替换样本须重新检查音频参数、标签和测试，更新 `SHA256SUMS.txt` 与本说明；不可仅改哈希来掩盖未知样本变动。

## 验证证据

1. 引入 fixture 引用但尚未创建文件时，5 个针对性用例全部按预期失败：缺少样本。未新增 Skip。
2. 补齐固定样本、复制配置与校验清单后，2026-10-08 Windows 完整 Core 测试首次 **52/52 通过**；主线并行新增用例后再次完整运行，最终 **53/53 通过、0 失败、0 跳过**，构建无警告。
3. 显式指定 FFmpeg **原生** MP3/Opus 解码器，两个样本完整解码退出码均为 0：

```powershell
$env:MFC_ENGINE_DIR = (Resolve-Path 'tools/win-x64').Path
dotnet test tests/MusicFormatConverter.Core.Tests/MusicFormatConverter.Core.Tests.csproj -c Release

& tools/win-x64/ffmpeg.exe -v error -nostdin -c:a mp3 `
    -i tests/MusicFormatConverter.Core.Tests/Fixtures/sine-997hz.mp3 -map 0:a:0 -f null -
& tools/win-x64/ffmpeg.exe -v error -nostdin -c:a opus `
    -i tests/MusicFormatConverter.Core.Tests/Fixtures/sine-997hz.opus -map 0:a:0 -f null -
```

macOS 最小构建的实际整套运行仍需由对应机器/CI 执行；Windows 上的原生解码成功不冒充 macOS 真机验收。此次不改生产代码、根配置或打包逻辑。
