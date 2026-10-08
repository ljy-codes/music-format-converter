#!/usr/bin/env python3
"""Build-time manifest and real FFmpeg checks. No downloader and no runtime hook."""
import argparse
import hashlib
import json
import pathlib
import platform
import subprocess
import tempfile

ROOT = pathlib.Path(__file__).resolve().parent.parent
LOCK = json.loads((ROOT / "scripts/engine-lock.json").read_text(encoding="utf-8"))["macos"]


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def run(*args):
    return subprocess.run(args, check=True, text=True, stdout=subprocess.PIPE,
                          stderr=subprocess.PIPE, timeout=120).stdout


def write_manifest(directory, runtime):
    files = [{"path": p.relative_to(directory).as_posix(), "sha256": sha(p)}
             for p in sorted(directory.rglob("*"))
             if p.is_file() and p.name != "engine-manifest.json"]
    manifest = {"schema": 1, "runtime": runtime, "version": LOCK["version"],
                "sourceSha256": LOCK["sourceSha256"], "lame": LOCK["lame"], "files": files}
    (directory / "engine-manifest.json").write_text(
        json.dumps(manifest, indent=2) + "\n", encoding="utf-8")


def check(directory, runtime):
    manifest = json.loads((directory / "engine-manifest.json").read_text(encoding="utf-8"))
    if (manifest["runtime"], manifest["version"], manifest["sourceSha256"]) != (
            runtime, LOCK["version"], LOCK["sourceSha256"]):
        raise ValueError("Engine manifest does not match source lock/runtime")
    if manifest.get("lame") != LOCK["lame"]:
        raise ValueError("LAME manifest differs from source lock")
    seen = set()
    for entry in manifest["files"]:
        path = directory / entry["path"]
        if not path.resolve().is_relative_to(directory.resolve()) or entry["path"] in seen:
            raise ValueError("Unsafe or duplicate manifest path")
        seen.add(entry["path"])
        if sha(path) != entry["sha256"]:
            raise ValueError(f"Engine hash mismatch: {path}")
    required = {"ffmpeg", "ffprobe", "LICENSE.txt", "licenses/COPYING.LGPLv2.1",
                "licenses/LICENSE.md", "provenance/configure.txt",
                "licenses/LAME-COPYING", "licenses/LAME-LICENSE", "provenance/lame-configure.txt", "provenance/lame-macos.patch",
                f"provenance/sources/lame-{LOCK['lame']['version']}.tar.gz",
                f"provenance/sources/ffmpeg-{LOCK['version']}.tar.xz"}
    if not required.issubset(seen):
        raise ValueError(f"Missing hashed engine components: {required - seen}")
    if not any(name.endswith(".dylib") for name in seen):
        raise ValueError("Shared libraries missing")
    if sha(directory / f"provenance/sources/ffmpeg-{LOCK['version']}.tar.xz") != LOCK["sourceSha256"]:
        raise ValueError("Corresponding source differs from lock")
    if sha(directory / f"provenance/sources/lame-{LOCK['lame']['version']}.tar.gz") != LOCK["lame"]["sourceSha256"]:
        raise ValueError("LAME corresponding source differs from lock")
    if "GNU LESSER GENERAL PUBLIC LICENSE" not in (directory / "LICENSE.txt").read_text():
        raise ValueError("Missing LGPL license")
    expected_arch = {"osx-arm64": "arm64", "osx-x64": "x86_64"}[runtime]
    if platform.system() != "Darwin" or platform.machine() != expected_arch:
        raise ValueError("Must test on matching native macOS architecture")
    if not (directory / "libmp3lame.dylib").is_file():
        raise ValueError("Shared MP3 encoder missing")
    for binary in [directory / "ffmpeg", directory / "ffprobe", *directory.glob("*.dylib")]:
        if expected_arch not in run("lipo", "-archs", str(binary)).split():
            raise ValueError(f"Wrong architecture: {binary}")
        for line in run("otool", "-L", str(binary)).splitlines()[1:]:
            dependency = line.strip().split(" (", 1)[0]
            if dependency.startswith("@loader_path/"):
                if not (binary.parent / dependency.removeprefix("@loader_path/")).is_file():
                    raise ValueError(f"Missing dynamic dependency: {dependency}")
            elif not dependency.startswith(("/usr/lib/", "/System/Library/")):
                raise ValueError(f"Nonportable dynamic dependency: {dependency}")
    for tool in ("ffmpeg", "ffprobe"):
        version = run(str(directory / tool), "-version")
        if not version.startswith(f"{tool} version {LOCK['version']} "):
            raise ValueError(f"Unexpected {tool} version")
        for flag in ("--disable-gpl", "--disable-nonfree", "--disable-network", "--disable-autodetect"):
            if flag not in version:
                raise ValueError(f"Missing license/offline configuration: {flag}")
        if "--enable-gpl" in version or "--enable-nonfree" in version:
            raise ValueError("Forbidden engine configuration")
    ffmpeg, ffprobe = str(directory / "ffmpeg"), str(directory / "ffprobe")
    encoders = run(ffmpeg, "-hide_banner", "-encoders")
    for encoder in ("alac", "aac", "libmp3lame", "flac", "pcm_s24le", "png", "mjpeg"):
        if not any(len(parts := line.split()) > 1 and parts[1] == encoder for line in encoders.splitlines()):
            raise ValueError(f"Required encoder missing: {encoder}")
    decoders = run(ffmpeg, "-hide_banner", "-decoders")
    # Keep broad built-in decoding, rather than a tiny format whitelist build.
    for decoder in ("flac", "alac", "aac", "mp3", "opus", "vorbis", "wavpack", "ape", "wmav2", "pcm_s24le", "png", "mjpeg"):
        if not any(len(parts := line.split()) > 1 and parts[1] == decoder
                   for line in decoders.splitlines()):
            raise ValueError(f"Required decoder missing: {decoder}")
    with tempfile.TemporaryDirectory(prefix="mfc-engine-") as temp:
        temp = pathlib.Path(temp)
        source, result = str(temp / "source.flac"), str(temp / "result.m4a")
        run(ffmpeg, "-nostdin", "-v", "error", "-f", "lavfi", "-i",
            "sine=frequency=997:sample_rate=48000:duration=0.2", "-c:a", "flac", source)
        run(ffmpeg, "-nostdin", "-v", "error", "-i", source, "-c:a", "alac", result)
        probe = json.loads(run(ffprobe, "-v", "error", "-show_streams", "-of", "json", result))
        audio = next(stream for stream in probe["streams"] if stream["codec_type"] == "audio")
        if (audio["codec_name"], audio["sample_rate"]) != ("alac", "48000"):
            raise ValueError("ALAC roundtrip metadata mismatch")
        for file, output in ((source, "source.pcm"), (result, "result.pcm")):
            run(ffmpeg, "-nostdin", "-v", "error", "-i", file, "-map", "0:a:0",
                "-f", "s16le", str(temp / output))
        if sha(temp / "source.pcm") != sha(temp / "result.pcm"):
            raise ValueError("Lossless PCM roundtrip mismatch")
        run(ffmpeg, "-nostdin", "-v", "error", "-i", source, "-c:a", "aac", str(temp / "aac.m4a"))
        run(ffmpeg, "-nostdin", "-v", "error", "-i", source, "-c:a", "libmp3lame", "-b:a", "320k", str(temp / "mp3.mp3"))
    print(f"Verified native {runtime} engine, hashes, dependencies and real FLAC/ALAC/AAC/MP3 conversions.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("command", choices=["manifest", "check"])
    parser.add_argument("directory", type=pathlib.Path)
    parser.add_argument("runtime", choices=["osx-arm64", "osx-x64"])
    args = parser.parse_args()
    (write_manifest if args.command == "manifest" else check)(args.directory.resolve(), args.runtime)
