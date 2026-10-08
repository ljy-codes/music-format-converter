#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:?Usage: bash scripts/build-engine-macos.sh osx-arm64|osx-x64}"
[[ "$(uname -s)" == Darwin ]] || { echo "Build on a real Mac, not Windows/Linux."; exit 1; }
case "$RID" in
  osx-arm64) ARCH=arm64 ;;
  osx-x64) ARCH=x86_64 ;;
  *) echo "Unsupported runtime: $RID"; exit 1 ;;
esac
[[ "$(uname -m)" == "$ARCH" ]] || { echo "Use a matching native runner, not cross compilation/Rosetta."; exit 1; }
for tool in python3 clang make tar curl shasum install_name_tool otool lipo; do command -v "$tool" >/dev/null; done
VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["version"])' "$ROOT/scripts/engine-lock.json")"
SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["sourceSha256"])' "$ROOT/scripts/engine-lock.json")"
URL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["sourceUrl"])' "$ROOT/scripts/engine-lock.json")"
CACHE="$ROOT/artifacts/engine-cache"
mkdir -p "$CACHE" "$ROOT/artifacts/build" "$ROOT/tools"
ARCHIVE="$CACHE/ffmpeg-$VERSION.tar.xz"
if [[ ! -f "$ARCHIVE" ]]; then
  CURL_ARGS=(--fail --location --retry 3)
  [[ -z "${MFC_DOWNLOAD_PROXY:-}" ]] || CURL_ARGS+=(--proxy "$MFC_DOWNLOAD_PROXY")
  curl "${CURL_ARGS[@]}" "$URL" -o "$ARCHIVE.partial"
  [[ "$(shasum -a 256 "$ARCHIVE.partial" | awk '{print $1}')" == "$SHA" ]] || { echo "Source hash mismatch"; exit 1; }
  mv "$ARCHIVE.partial" "$ARCHIVE"
fi
[[ "$(shasum -a 256 "$ARCHIVE" | awk '{print $1}')" == "$SHA" ]] || { echo "Cached source hash mismatch"; exit 1; }
# Fresh build paths: never reuse contaminated configure state or remove user files.
WORK="$(mktemp -d "$ROOT/artifacts/build/ffmpeg-$RID.XXXXXX")"
tar -xJf "$ARCHIVE" -C "$WORK"
PREFIX="$WORK/install"
STAGE="$WORK/engine"
mkdir -p "$STAGE/licenses" "$STAGE/provenance/sources"
cd "$WORK/ffmpeg-$VERSION"
# Built-in decoders/demuxers/filters remain enabled. No Homebrew codec linkage.
# disable-x86asm avoids a NASM prerequisite on Intel at a performance cost.
export MACOSX_DEPLOYMENT_TARGET=14.0
CONFIGURE=(
  "--prefix=$PREFIX" "--arch=$ARCH" --target-os=darwin --cc=clang
  --enable-shared --disable-static "--install-name-dir=@loader_path"
  --disable-gpl --disable-nonfree --disable-version3 --disable-network
  --disable-autodetect --disable-doc --disable-debug --disable-x86asm
  --disable-programs --enable-ffmpeg --enable-ffprobe
)
printf '%q ' ./configure "${CONFIGURE[@]}" > "$STAGE/provenance/configure.txt"
printf '\n' >> "$STAGE/provenance/configure.txt"
./configure "${CONFIGURE[@]}"
make -j "${MFC_BUILD_JOBS:-$(sysctl -n hw.logicalcpu)}"
make install
cp "$PREFIX/bin/ffmpeg" "$PREFIX/bin/ffprobe" "$STAGE/"
cp -P "$PREFIX"/lib/*.dylib "$STAGE/"
cp COPYING.LGPLv2.1 "$STAGE/LICENSE.txt"
cp COPYING.LGPLv2.1 LICENSE.md "$STAGE/licenses/"
cp config.h ffbuild/config.mak "$STAGE/provenance/"
cp "$ARCHIVE" "$STAGE/provenance/sources/"
cp "$ROOT/scripts/engine-lock.json" "$ROOT/scripts/build-engine-macos.sh" "$STAGE/provenance/"
printf '%s\n' "Official FFmpeg $VERSION source; SHA256 $SHA" \
  "LGPL-2.1-or-later; no GPL/nonfree/version3/external autodetected libraries." \
  "Corresponding complete source, configure arguments and config files are bundled." \
  "The application launches a replaceable independent engine, without runtime downloads." > "$STAGE/provenance/NOTICE.txt"
"$STAGE/ffmpeg" -version > "$STAGE/provenance/ffmpeg-version.txt"
python3 "$ROOT/scripts/macos-engine.py" manifest "$STAGE" "$RID"
python3 "$ROOT/scripts/macos-engine.py" check "$STAGE" "$RID"
DEST="$ROOT/tools/$RID"
# No merge into an existing directory: stale dylibs or symlinks must not survive.
if [[ -e "$DEST" ]]; then
  echo "Engine verified in $STAGE, but $DEST already exists; move it aside explicitly, then retry."
  exit 1
fi
mv "$STAGE" "$DEST"
echo "Verified native engine: $DEST"
