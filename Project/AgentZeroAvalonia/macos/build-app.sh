#!/bin/sh
# Wrap a `dotnet publish -r osx-arm64` (or osx-x64) output folder into AgentZeroLite.app
# and ad-hoc sign it (M0040). Developer ID signing + notarization are a separate step,
# see harness/knowledge/_shared/code-signing.md.
#
# Usage: build-app.sh <publish-dir> <out.app> [version]
#   e.g. build-app.sh publish-avalonia/osx-arm64 publish-avalonia/AgentZeroLite.app 0.25.0
set -eu

src="${1:?publish dir}"
app="${2:?output .app path}"
version="${3:-}"
here="$(cd "$(dirname "$0")" && pwd)"

if [ ! -f "$src/AgentZeroLite" ]; then
  echo "[build-app] no AgentZeroLite executable in $src (publish with -p:UseAppHost=true)" >&2
  exit 1
fi
if [ -z "$version" ]; then
  if [ -f "$src/version.txt" ]; then version="$(tr -d '[:space:]' < "$src/version.txt")"; else version="0.0.0"; fi
fi

rm -rf "$app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

# Everything the publish produced goes next to the executable: managed assemblies,
# the native Avalonia/Skia/HarfBuzz dylibs, Porta.Pty's libporta_pty.dylib, the
# xterm bundle (Wasm/), the CLI wrapper.
cp -R "$src"/. "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/AgentZeroLite"
[ -f "$app/Contents/MacOS/AgentZeroLite.sh" ] && chmod +x "$app/Contents/MacOS/AgentZeroLite.sh"

sed -e "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"
printf 'APPL????' > "$app/Contents/PkgInfo"
[ -f "$here/AgentZeroLite.icns" ] && cp "$here/AgentZeroLite.icns" "$app/Contents/Resources/"

# Ad-hoc signature: enough to launch locally after `xattr -dr com.apple.quarantine`.
# --deep signs the bundled dylibs too; the entitlements keep JIT/unsigned-memory for
# the .NET runtime and allow the loopback asset server + outbound HTTP.
if command -v codesign >/dev/null 2>&1; then
  codesign --force --deep --sign - --entitlements "$here/entitlements.plist" --options runtime "$app" \
    || codesign --force --deep --sign - "$app"
  codesign --verify --deep --strict "$app" && echo "[build-app] ad-hoc signed: $app"
else
  echo "[build-app] codesign not available - bundle left unsigned" >&2
fi
echo "[build-app] $app (version $version)"
