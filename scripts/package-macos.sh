#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
RID="${1:?Usage: bash scripts/package-macos.sh osx-arm64|osx-x64}"
[[ "$(uname -s)" == Darwin ]] || { echo "Only native macOS packaging is supported."; exit 1; }
case "$RID" in
  osx-arm64) ARCH=arm64 ;;
  osx-x64) ARCH=x86_64 ;;
  *) echo "Unsupported runtime"; exit 1 ;;
esac
[[ "$(uname -m)" == "$ARCH" ]] || { echo "Native architecture mismatch"; exit 1; }
VERSION="$(python3 -c 'import sys,xml.etree.ElementTree as E; print(E.parse(sys.argv[1]).findtext(".//Version"))' "$ROOT/Directory.Build.props")"
ENGINE="$ROOT/tools/$RID"
python3 "$ROOT/scripts/macos-engine.py" check "$ENGINE" "$RID"
cd "$ROOT"
[[ "$(dotnet --version)" == 10.0.400 ]] || { echo ".NET SDK 10.0.400 required"; exit 1; }
mkdir -p "$ROOT/artifacts/build" "$ROOT/artifacts/installer"
MFC_ENGINE_DIR="$ENGINE" dotnet test "$ROOT/MusicFormatConverter.sln" -c Release \
  --logger trx --results-directory "$ROOT/artifacts/tests/$RID"
WORK="$(mktemp -d "$ROOT/artifacts/build/package-$RID.XXXXXX")"
APP="$WORK/音乐格式转换器.app"
MACOS="$APP/Contents/MacOS"
mkdir -p "$MACOS" "$APP/Contents/Resources"
dotnet publish "$ROOT/src/MusicFormatConverter.App/MusicFormatConverter.App.csproj" \
  -c Release -r "$RID" --self-contained true -o "$MACOS" \
  -p:Version="$VERSION" -p:PublishSingleFile=false -p:PublishTrimmed=false \
  -p:DebugType=None -p:DebugSymbols=false
for required in MusicFormatConverter.App MusicFormatConverter.App.dll libcoreclr.dylib libhostfxr.dylib; do
  [[ -f "$MACOS/$required" ]] || { echo "Missing self-contained app component: $required"; exit 1; }
done
lipo -verify_arch "$ARCH" "$MACOS/MusicFormatConverter.App"
cp -R "$ENGINE" "$MACOS/tools"
cp "$ROOT/docs/engine-provenance.md" "$ROOT/docs/packaging.md" "$APP/Contents/Resources/"
cp "$ROOT/README.md" "$ROOT/LICENSE" "$ROOT/THIRD-PARTY-NOTICES.md" "$ROOT/docs/implementation.md" "$APP/Contents/Resources/"
cp "$ROOT/installer/macos/Info.plist" "$APP/Contents/Info.plist"
cp "$ROOT/src/MusicFormatConverter.App/Assets/app-icon.icns" "$APP/Contents/Resources/app-icon.icns"
python3 "$ROOT/scripts/package-notices.py" \
  "$ROOT/src/MusicFormatConverter.App/obj/project.assets.json" "$APP/Contents/Resources/third-party"
/usr/libexec/PlistBuddy -c "Set :CFBundleShortVersionString $VERSION" "$APP/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $VERSION" "$APP/Contents/Info.plist"
plutil -lint "$APP/Contents/Info.plist"
chmod +x "$MACOS/MusicFormatConverter.App" "$MACOS/tools/ffmpeg" "$MACOS/tools/ffprobe"

# Signing is opt-in. Default '-' means ad-hoc, NOT Developer ID/notarized.
IDENTITY="${MFC_CODESIGN_IDENTITY:--}"
LABEL=unsigned
SIGN_ARGS=(--force --sign "$IDENTITY")
if [[ "$IDENTITY" != "-" ]]; then
  LABEL=signed
  SIGN_ARGS+=(--timestamp --options runtime)
fi
# Sign inside-out; do not use --deep to hide nested signing mistakes.
while IFS= read -r -d '' binary; do
  if file -b "$binary" | grep -q 'Mach-O'; then
    codesign "${SIGN_ARGS[@]}" --entitlements "$ROOT/installer/macos/entitlements.plist" "$binary"
  fi
done < <(find "$MACOS" -type f -print0)
# Signatures change engine bytes; seal the signed copy and validate it again.
python3 "$ROOT/scripts/macos-engine.py" manifest "$MACOS/tools" "$RID"
python3 "$ROOT/scripts/macos-engine.py" check "$MACOS/tools" "$RID"
codesign "${SIGN_ARGS[@]}" --entitlements "$ROOT/installer/macos/entitlements.plist" "$APP"
codesign --verify --deep --strict --verbose=2 "$APP"
if [[ -n "${MFC_NOTARY_PROFILE:-}" ]]; then
  [[ "$IDENTITY" != "-" ]] || { echo "Notarization requires a Developer ID signing identity"; exit 1; }
  ditto -c -k --keepParent "$APP" "$WORK/notarize.zip"
  xcrun notarytool submit "$WORK/notarize.zip" --keychain-profile "$MFC_NOTARY_PROFILE" --wait
  xcrun stapler staple "$APP"
  xcrun stapler validate "$APP"
  LABEL=notarized
fi
DMGROOT="$WORK/dmg"
mkdir -p "$DMGROOT"
cp -R "$APP" "$DMGROOT/"
ln -s /Applications "$DMGROOT/Applications"
printf '%s\n' "音乐格式转换器 $VERSION — $RID" "Signing status: $LABEL" \
  "Unsigned means no Developer ID signature/notarization (ad-hoc signing only)." \
  "Do not disable system-wide Gatekeeper. Review publisher and SHA256 first." > "$DMGROOT/READ-ME.txt"
DMG="$ROOT/artifacts/installer/MusicFormatConverter-$VERSION-$RID-$LABEL.dmg"
hdiutil create -volname "音乐格式转换器 $VERSION" -srcfolder "$DMGROOT" -format UDZO -ov "$DMG"
hdiutil verify "$DMG"
if [[ "$IDENTITY" != "-" ]]; then codesign --force --sign "$IDENTITY" --timestamp "$DMG"; fi
if [[ -n "${MFC_NOTARY_PROFILE:-}" ]]; then
  xcrun notarytool submit "$DMG" --keychain-profile "$MFC_NOTARY_PROFILE" --wait
  xcrun stapler staple "$DMG"
  xcrun stapler validate "$DMG"
fi
(cd "$(dirname "$DMG")" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
python3 "$ROOT/scripts/test-macos-package.py" "$DMG" "$RID" "$ROOT/artifacts/installer/validation-$RID.json"
echo "Native app: $APP"
echo "DMG ($LABEL): $DMG"
echo "Not installed or released. Only explicit MFC_NOTARY_PROFILE opts into Apple notarization upload."
