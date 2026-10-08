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
LAME_VERSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["lame"]["version"])' "$ROOT/scripts/engine-lock.json")"
LAME_SHA="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["lame"]["sourceSha256"])' "$ROOT/scripts/engine-lock.json")"
LAME_URL="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["macos"]["lame"]["sourceUrl"])' "$ROOT/scripts/engine-lock.json")"
CACHE="$ROOT/artifacts/engine-cache"
mkdir -p "$CACHE" "$ROOT/artifacts/build" "$ROOT/tools"
ARCHIVE="$CACHE/ffmpeg-$VERSION.tar.xz"
fetch_source() {
  local url="$1" archive="$2" sha="$3"
  local -a CURL_ARGS=(--fail --location --retry 3 --connect-timeout 30 --max-time 600)
  [[ -z "${MFC_DOWNLOAD_PROXY:-}" ]] || CURL_ARGS+=(--proxy "$MFC_DOWNLOAD_PROXY")
  if [[ ! -f "$archive" ]]; then
    curl "${CURL_ARGS[@]}" "$url" -o "$archive.partial"
    [[ "$(shasum -a 256 "$archive.partial" | awk '{print $1}')" == "$sha" ]] || { echo "Source hash mismatch: $archive"; exit 1; }
    mv "$archive.partial" "$archive"
  fi
  [[ "$(shasum -a 256 "$archive" | awk '{print $1}')" == "$sha" ]] || { echo "Cached source hash mismatch: $archive"; exit 1; }
}
fetch_source "$URL" "$ARCHIVE" "$SHA"
LAME_ARCHIVE="$CACHE/lame-$LAME_VERSION.tar.gz"
fetch_source "$LAME_URL" "$LAME_ARCHIVE" "$LAME_SHA"
# Fresh build paths: never reuse contaminated configure state or remove user files.
WORK="$(mktemp -d "$ROOT/artifacts/build/ffmpeg-$RID.XXXXXX")"
tar -xJf "$ARCHIVE" -C "$WORK"
tar -xzf "$LAME_ARCHIVE" -C "$WORK"
PREFIX="$WORK/install"
STAGE="$WORK/engine"
mkdir -p "$STAGE/licenses" "$STAGE/provenance/sources"
export MACOSX_DEPLOYMENT_TARGET=14.0
# LAME is the only external codec; build it shared so users can replace it.
cd "$WORK/lame-$LAME_VERSION"
LAME_CONFIGURE=("--prefix=$PREFIX" --enable-shared --disable-static --disable-frontend --disable-dependency-tracking)
printf '%q ' ./configure "${LAME_CONFIGURE[@]}" > "$STAGE/provenance/lame-configure.txt"
printf '\n' >> "$STAGE/provenance/lame-configure.txt"
./configure "${LAME_CONFIGURE[@]}"
make -j "${MFC_BUILD_JOBS:-$(sysctl -n hw.logicalcpu)}"
make install
cp COPYING "$STAGE/licenses/LAME-COPYING"
cp LICENSE "$STAGE/licenses/LAME-LICENSE"
cp config.h "$STAGE/provenance/lame-config.h"
cp config.log "$STAGE/provenance/lame-config.log"
cp "$LAME_ARCHIVE" "$STAGE/provenance/sources/"
cd "$WORK/ffmpeg-$VERSION"
# Built-in decoders/demuxers/filters remain enabled. No Homebrew codec linkage.
# disable-x86asm avoids a NASM prerequisite on Intel at a performance cost.
CONFIGURE=(
  "--prefix=$PREFIX" "--arch=$ARCH" --target-os=darwin --cc=clang
  --enable-shared --disable-static "--install-name-dir=@loader_path"
  --disable-gpl --disable-nonfree --disable-version3 --disable-network
  --disable-autodetect --disable-doc --disable-debug --disable-x86asm
  --disable-programs --enable-ffmpeg --enable-ffprobe
  --enable-libmp3lame "--extra-cflags=-I$PREFIX/include" "--extra-ldflags=-L$PREFIX/lib"
)
printf '%q ' ./configure "${CONFIGURE[@]}" > "$STAGE/provenance/configure.txt"
printf '\n' >> "$STAGE/provenance/configure.txt"
./configure "${CONFIGURE[@]}"
make -j "${MFC_BUILD_JOBS:-$(sysctl -n hw.logicalcpu)}"
make install
cp "$PREFIX/bin/ffmpeg" "$PREFIX/bin/ffprobe" "$STAGE/"
cp -P "$PREFIX"/lib/*.dylib "$STAGE/"
# Replace build-directory install names in every dependent binary and library.
while IFS= read -r -d '' binary; do
  if [[ "$binary" == *.dylib ]]; then install_name_tool -id "@loader_path/$(basename "$binary")" "$binary"; fi
  while IFS= read -r dependency; do
    [[ "$dependency" != "$PREFIX/lib/"* ]] || install_name_tool -change "$dependency" "@loader_path/$(basename "$dependency")" "$binary"
  done < <(otool -L "$binary" | tail -n +2 | awk '{print $1}')
done < <(find "$STAGE" -maxdepth 1 -type f -print0)
cp COPYING.LGPLv2.1 "$STAGE/LICENSE.txt"
cp COPYING.LGPLv2.1 LICENSE.md "$STAGE/licenses/"
cp config.h ffbuild/config.mak "$STAGE/provenance/"
cp "$ARCHIVE" "$STAGE/provenance/sources/"
cp "$ROOT/scripts/engine-lock.json" "$ROOT/scripts/build-engine-macos.sh" "$STAGE/provenance/"
printf '%s\n' "Official FFmpeg $VERSION source; SHA256 $SHA" \
  "LGPL-2.1-or-later; LAME $LAME_VERSION shared MP3 encoder (LGPL-2.0-or-later)." \
  "No GPL/nonfree/version3/network or external autodetected libraries." \
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
