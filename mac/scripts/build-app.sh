#!/bin/bash
# ClipSync.app 을 조립하고 고정 서명 ID로 서명해 ~/Applications 에 설치한다 (spec D-38).
# 모든 Mac 빌드는 같은 서명 ID + 같은 --identifier 를 써야 pasteboard/TCC/Keychain 권한이 유지된다. ad-hoc 서명 금지.
set -euo pipefail

BUNDLE_ID="com.tobylee.clipsync"
# 자체 서명 "ClipSync Dev" (D-55). Apple Development 인증서(6ED55637…)는 2026-09-30 Apple이 폐기해
# "Malware Blocked and Moved to Bin"으로 실행이 막혔다. 자체 서명은 원격 폐기 대상이 아니다.
SIGN_SHA1="0877300230D8AF6664E87DA91F5F78A15269E140"   # ClipSync Dev (self-signed, login keychain)
CONFIG="${CONFIG:-release}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
PKG="$ROOT/ClipSyncApp"
OUT="$ROOT/build/ClipSync.app"
DEST="$HOME/Applications/ClipSync.app"

if ! security find-identity -v -p codesigning | grep -q "$SIGN_SHA1"; then
  echo "error: signing identity $SIGN_SHA1 not found in keychain" >&2; exit 1
fi

swift build --package-path "$PKG" -c "$CONFIG"
BIN="$(swift build --package-path "$PKG" -c "$CONFIG" --show-bin-path)/ClipSync"

rm -rf "$OUT"
mkdir -p "$OUT/Contents/MacOS" "$OUT/Contents/Resources"
cp "$BIN" "$OUT/Contents/MacOS/ClipSync"
cp "$PKG/Info.plist" "$OUT/Contents/Info.plist"

codesign --force --sign "$SIGN_SHA1" --identifier "$BUNDLE_ID" --options runtime --timestamp=none "$OUT"
codesign --verify --strict --verbose=2 "$OUT"

# 지정 요구사항이 인증서 기반이어야 재빌드 후에도 권한이 유지된다 (cdhash 기반이면 실패).
REQ="$(codesign -d -r- "$OUT" 2>&1 | grep '^designated' || true)"
echo "$REQ"
if echo "$REQ" | grep -q 'cdhash'; then echo "error: designated requirement is cdhash-based" >&2; exit 1; fi

if pgrep -x ClipSync >/dev/null; then pkill -x ClipSync; sleep 0.5; fi
mkdir -p "$HOME/Applications"
rm -rf "$DEST"
cp -R "$OUT" "$DEST"
echo "installed: $DEST"
