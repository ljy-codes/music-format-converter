# 引擎来源、哈希与再分发说明

## 固定来源（2026-10-08 获取记录）

权威机器锁文件为 `scripts/engine-lock.json`。不跟随 moving `latest`，不在 App 运行时下载。

### Windows x64：BtbN LGPL shared

| 项目 | 固定值 |
|---|---|
| 仓库 | `https://github.com/BtbN/FFmpeg-Builds` |
| release tag | `autobuild-2026-10-07-13-07` |
| asset | `ffmpeg-n8.1.3-14-g330caae0c1-win64-lgpl-shared-8.1.zip` |
| asset SHA256 | `30fdaaeb116730fdd6432c2663af0e9ecdf1d6cc387ac085cc1faf8494942510` |
| 上游 checksums.sha256 的 SHA256 | `3aeda19329874e49b312e96139492c6cb50dc4944b646afb2bb32371ae86bf85` |
| `ffmpeg -version` | `n8.1.3-14-g330caae0c1-20261007` |
| FFmpeg source revision | `330caae0c1acccd2222edc52a05940c574561ce5` |
| source `.tar.gz` SHA256 | `67b5876ee973a26f267280b2c1eb851a0ac7a502382d20e41b7a819111886af9` |
| FFmpeg-Builds recipe revision | `9acad4a9ef1583096af7836cc1e9c8cbcb4d3950` |
| recipe `.tar.gz` SHA256 | `27dca8db7b2466b6f5145168721264de91dc9b56f50bebc441249f92e4acf548` |

获取地址由以下固定模板拼成，下载脚本已经实现：

```text
https://github.com/BtbN/FFmpeg-Builds/releases/download/<tag>/<asset>
https://github.com/BtbN/FFmpeg-Builds/releases/download/<tag>/checksums.sha256
https://codeload.github.com/FFmpeg/FFmpeg/tar.gz/<source revision>
https://codeload.github.com/BtbN/FFmpeg-Builds/tar.gz/<recipe revision>
```

二进制哈希同时与上游发布的 checksum 行及 GitHub release asset digest 核对。锁中的源码/配方归档哈希为本次固定 commit 归档实际下载后计算的值，不声称上游另行发布了这两个归档的 SHA 签名。GitHub 日构建可能日后移除旧资产；请保存 `artifacts/engine-cache`，不能失败后自动改拿新版本。

**这个 Windows 版本不是纯官方 8.1.3 tarball 构建**：它位于 8.1.3 后的固定提交。不能拿 Mac 的 8.1.3 源码冒充它的对应源码。

共享 DLL 为：

```text
avcodec-62.dll
avdevice-62.dll
avfilter-11.dll
avformat-62.dll
avutil-60.dll
swresample-6.dll
swscale-9.dll
```

上游 `bin` 的全部 DLL 均复制；`ffmpeg.exe`、`ffprobe.exe` 保留，`ffplay.exe` 不进入分发清单。实际运行确认 DLL 可加载。包内 `provenance/ffmpeg-version.txt` 保存完整配置，有 `--enable-version3`、`--enable-shared`，没有 `--enable-gpl` / `--enable-nonfree`。

因此 Windows FFmpeg 适用 **LGPL-3.0-or-later**，不是 LGPL 2.1。`LICENSE.txt` 是上游附带文本；同时从精确源码提取 LGPLv3、其并入的 GPLv3 全文以及 LICENSE.md，不能误以为附带 GPLv3 文本就说明选择了 GPL 构建。

### Mac arm64 / x64：官方 FFmpeg 8.1.3 本地编译

| 项目 | 固定值 |
|---|---|
| 官方 source | `https://ffmpeg.org/releases/ffmpeg-8.1.3.tar.xz` |
| SHA256 | `7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3` |
| FFmpeg 版本 | `8.1.3` |
| 许可配置 | `--disable-gpl --disable-nonfree --disable-version3` |
| 外部依赖/联网配置 | `--disable-autodetect --disable-network` |

此 SHA 为从官方 HTTPS 固定 tarball 实际下载后计算并锁定，未在本任务验证官方 detached GPG 签名，不把哈希等同于发行人数字签名。脚本不声称此版本是未来运行时的“最新”版本。

编译时保留 FFmpeg 内建常见音频解码/编码能力；0.1.1 仅显式加入 LAME 3.100 共享 MP3 编码器，不启用其他外部 codec 自动探测。采用 LGPL-2.1-or-later shared 构建，保留 LGPLv2.1 与 LICENSE.md。源码 tarball、构建脚本、configure 参数、config.h、config.mak 全部随引擎放在 `provenance` 中。Mac 二进制由具体 Mac/toolchain 编译，其 SHA 无法提前伪造；成功后由 `engine-manifest.json` 记录，App 签名更改字节后重新生成分发副本清单。

## 包内可追溯内容

```text
tools/
  ffmpeg[.exe], ffprobe[.exe], 所有必要 DLL/dylib
  LICENSE.txt
  licenses/
  engine-manifest.json
  provenance/
    engine-lock.json
    ffmpeg-version.txt
    NOTICE.txt
    sources/对应 FFmpeg 源码归档
    # Windows: 上游 checksums、精确构建配方归档
    # Mac: configure 参数、构建脚本、配置结果
```

本程序作为独立进程调用 FFmpeg，不链接 FFmpeg 到应用程序集；保持工具可替换。不得移除用户为调试 LGPL 修改而合理需要的能力。哈希检查发生于开发/打包阶段，不作为 App 运行时禁止用户替换引擎的手段。

## 再分发边界与待办（不作法律完成声明）

1. 本次交付包含实际许可证、FFmpeg 对应源码与上游配方，不是只有网页链接。
2. **BtbN 大型构建还含第三方依赖**。配方归档给出来源、版本选择、补丁和构建指令，但不等于已归档所有依赖的完整源代码和许可。个别配方可能依赖移动 upstream HEAD；仅凭 tag 无法保证第三方源代码逐字重建。
3. 公开分发 Windows 二进制前，维护者应补齐对应版本第三方 notices/source mirror（含静态链接进共享 DLL 的依赖），审核许可与源代码提供方式，并与二进制同站提供可获取的完整对应源码。若无法取得完整依赖对应源码，应改用可重建、依赖更少的受控 LGPL 构建，而不是声称当前配方已满足全部义务。
4. 包内源码是 FFmpeg 对应 source baseline，加上固定构建配方/补丁；第三方完整对应源码审计未完成，所以本次脚本用于本地打包/内部验收，没有自动公开 Release。
5. Mac 关闭外部依赖自动探测，仅显式启用锁定 LAME；原生构建日志、测试与产物由现有打包工作流留存，发布附件含逐架构验证记录。Apple Music 导入与 Developer ID 公证另行验证。
6. Windows 上游引擎未禁用网络协议；应用自身必须只传入受控本地文件，不应把不可信 URL 交给该引擎。“无运行时下载”不是声称 Windows FFmpeg 二进制不具备网络能力。
7. 固定哈希防止下载损坏/意外漂移，不是恶意软件安全证明，也不替代安全更新、签名、供应链或许可审查。包升级需要显式更新 lock、复核许可/源码并重跑真实测试。

## Mac 0.1.1：LAME 共享 MP3 编码器

- 版本：3.100。官方来源：`https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz`。
- SHA256：`ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e`。
- 许可：LGPL-2.0-or-later；原始 `COPYING` 与 `LICENSE` 保存在 `tools/licenses/LAME-*`。
- 只构建共享库，关闭命令行前端、静态库及额外依赖；FFmpeg 使用 `--enable-libmp3lame`。
- 源码 tarball、configure 参数与配置日志一并保存；dylib 安装名改为 `@loader_path`，不引用构建机或 Homebrew 路径。
- 在两个架构上实际执行 320 kbps MP3 编码和完整解码；缺编码器或可重定位依赖时拒绝发布。

Mac 使用随包 `lame-macos.patch` 删除 LAME 导出表中已被上游设为 static 的废弃 `lame_init_old`，修复现代 Apple 链接器错误。受支持的 `lame_init` 与编码实现保持不变；原始源码加该补丁构成对应源码。
