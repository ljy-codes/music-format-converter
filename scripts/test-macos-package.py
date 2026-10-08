#!/usr/bin/env python3
"""Mount the real DMG, copy out the app, validate and run it outside the build tree."""
import argparse
import base64
import hashlib
import json
import os
import pathlib
import plistlib
import shutil
import subprocess
import tempfile
import time

ROOT = pathlib.Path(__file__).resolve().parent.parent


def run(*args, env=None):
    result = subprocess.run(args, env=env, capture_output=True, text=True, timeout=180)
    if result.returncode:
        raise RuntimeError(f"{args[0]} exited {result.returncode}: {result.stderr[-4000:]}")
    return result.stdout


def check(dmg, runtime, report):
    with tempfile.TemporaryDirectory(prefix="mfc-package-") as temporary:
        temporary = pathlib.Path(temporary).resolve()
        mount = temporary / "mounted"
        run("hdiutil", "attach", "-readonly", "-nobrowse", "-mountpoint", str(mount), str(dmg))
        try:
            source = mount / "音乐格式转换器.app"
            app = temporary / source.name
            run("ditto", str(source), str(app))
            if not (mount / "Applications").is_symlink():
                raise ValueError("DMG lacks the Applications install shortcut")
        finally:
            run("hdiutil", "detach", str(mount))
        macos = app / "Contents/MacOS"
        executable = macos / "MusicFormatConverter.App"
        info = plistlib.loads((app / "Contents/Info.plist").read_bytes())
        if info["CFBundleExecutable"] != executable.name or info["LSMinimumSystemVersion"] != "14.0":
            raise ValueError("Invalid bundle metadata")
        architecture = {"osx-arm64": "arm64", "osx-x64": "x86_64"}[runtime]
        for file in macos.rglob("*"):
            if file.is_file() and "Mach-O" in run("file", "-b", str(file)):
                run("lipo", "-verify_arch", architecture, str(file))
        run("codesign", "--verify", "--deep", "--strict", str(app))
        engine = macos / "tools"
        run("python3", str(ROOT / "scripts/macos-engine.py"), "check", str(engine), runtime)
        if not (app / "Contents/Resources/third-party/packages.json").is_file():
            raise ValueError("Package notices missing")
        # Publish only a temporary CLI harness, using precisely the engine shipped in the app.
        cli = temporary / "cli"
        run("dotnet", "publish", str(ROOT / "src/MusicFormatConverter.Cli"), "-c", "Release",
            "-r", runtime, "--self-contained", "true", "-o", str(cli))
        environment = dict(os.environ, MFC_ENGINE_DIR=str(engine))
        for key in ("DYLD_LIBRARY_PATH", "DYLD_FALLBACK_LIBRARY_PATH"):
            environment.pop(key, None)
        ffmpeg, ffprobe = str(engine / "ffmpeg"), str(engine / "ffprobe")
        source = temporary / "中文歌曲_.flac"
        run(ffmpeg, "-v", "error", "-f", "lavfi", "-i", "sine=frequency=997:sample_rate=48000:duration=1",
            "-ac", "2", "-metadata", "title=Mac安装包测试", "-c:a", "flac", str(source), env=environment)
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        modes = {}
        for mode, codec in (("smart", "alac"), ("alac", "alac"), ("aac", "aac"),
                            ("mp3", "mp3"), ("flac", "flac"), ("wav", "pcm_s16le")):
            output = temporary / mode
            results = json.loads(run(str(cli / "MusicFormatConverter.Cli"), "convert", "--mode", mode,
                                     "--output", str(output), str(source), env=environment))
            result = results[0]
            if len(results) != 1 or result["status"] != "Succeeded":
                raise ValueError(f"{mode} conversion failed: {results}")
            audio = result["outputPath"]
            probe = json.loads(run(ffprobe, "-v", "error", "-show_streams", "-of", "json", audio, env=environment))
            stream = next(stream for stream in probe["streams"] if stream["codec_type"] == "audio")
            if (stream["codec_name"], stream["sample_rate"], stream["channels"]) != (codec, "48000", 2):
                raise ValueError(f"Incorrect output parameters for {mode}")
            run(ffmpeg, "-v", "error", "-xerror", "-i", audio, "-f", "null", "-", env=environment)
            if mode in ("smart", "alac", "flac", "wav"):
                def pcm_hash(path):
                    return run(ffmpeg, "-v", "error", "-i", str(path), "-map", "0:a:0", "-c:a", "pcm_s32le",
                               "-f", "hash", "-hash", "sha256", "-", env=environment).strip()
                if pcm_hash(source) != pcm_hash(audio):
                    raise ValueError(f"PCM mismatch for {mode}")
            modes[mode] = {"status": "Succeeded", "codec": codec, "sampleRate": 48000, "channels": 2}
        if hashlib.sha256(source.read_bytes()).hexdigest() != source_hash:
            raise ValueError("Source file was changed")
        environment.pop("MFC_ENGINE_DIR")
        with (temporary / "app.log").open("w+") as log:
            process = subprocess.Popen([str(executable)], cwd=temporary, env=environment, stdout=log, stderr=log)
            try:
                time.sleep(10)
                if process.poll() is not None:
                    log.seek(0)
                    raise ValueError(f"Native app quit during startup: {log.read()[-4000:]}")
            finally:
                if process.poll() is None:
                    process.terminate()
                    try:
                        process.wait(timeout=15)
                    except subprocess.TimeoutExpired:
                        process.kill()
                        process.wait()
        result = {"runtime": runtime, "version": info["CFBundleShortVersionString"],
                  "dmgSha256": hashlib.sha256(dmg.read_bytes()).hexdigest(),
                  "bundleSignature": "verified", "nativeStartup": "passed", "modes": modes,
                  "sourceUnchanged": True, "appleMusicImport": "not tested", "notarized": False}
        report.parent.mkdir(parents=True, exist_ok=True)
        report.write_text(json.dumps(result, indent=2) + "\n")
        print(json.dumps(result, indent=2))
        print("MFC_PACKAGE_VALIDATION=" + base64.b64encode(json.dumps(result).encode()).decode())


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("dmg", type=pathlib.Path)
    parser.add_argument("runtime", choices=["osx-arm64", "osx-x64"])
    parser.add_argument("report", type=pathlib.Path)
    args = parser.parse_args()
    check(args.dmg.resolve(), args.runtime, args.report)
