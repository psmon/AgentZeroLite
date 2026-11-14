#!/bin/sh
# Mac App Store build: publish folder -> sandboxed AgentZeroLite.app -> signed .pkg for upload.
#
# Usage: build-appstore.sh <publish-dir> <out-dir> [version] [build-number]
#   e.g. build-appstore.sh publish-avalonia/osx-arm64-mas publish-avalonia/appstore 0.25.8 1
#   The publish must leave Remote out: dotnet publish … -r osx-arm64 -p:IncludeRemote=false
#
# Environment:
#   MAS_SIGN_ID     "Apple Distribution: <Team> (<TEAMID>)"            (required)
#   MAS_PKG_ID      "3rd Party Mac Developer Installer: <Team> (<TEAMID>)" (required)
#   MAS_PROFILE     path to the MAC_APP_STORE .provisionprofile        (required)
#   MAS_SANDBOX_TEST=1  ad-hoc sign with the same entitlements, no profile, no .pkg — runs locally
#                       to check the sandboxed app before an upload
#
# Differences from the Developer ID bundle (build-app.sh), each one measured:
#   - Contents/MacOS/AgentZeroLite is the AppKit-linked launcher (launcher/launcher.c), not the
#     .NET apphost: sandboxed, the apphost's late AppKit load aborts the GUI at start.
#   - macos/appstore/entitlements.plist (App Sandbox + the terminal's temporary exceptions).
#   - the provisioning profile is embedded; no hardened runtime, no notarization (App Review).
set -eu

src="${1:?publish dir}"
out="${2:?output dir}"
version="${3:-}"
build="${4:-1}"
here="$(cd "$(dirname "$0")" && pwd)"
macos="$(cd "$here/.." && pwd)"

if [ -z "$version" ] && [ -f "$src/version.txt" ]; then version="$(tr -d '[:space:]' < "$src/version.txt")"; fi
version="${version:-0.0.0}"

# Remote (the terminals from a browser) is not part of the App Store edition: publish with
# -p:IncludeRemote=false. A publish that still carries its web client was made for the
# Developer ID .pkg and would ship a LAN listener App Review has to ask about.
if [ -d "$src/Wasm/remote" ]; then
  echo "error: $src was published with Remote — publish again with -p:IncludeRemote=false" >&2
  exit 1
fi

mkdir -p "$out"
app="$out/AgentZeroLite.app"

# 1) The ordinary bundle (ad-hoc signed; every signature is replaced below).
sh "$macos/build-app.sh" "$src" "$app" "$version" >/dev/null

# 2) Launcher in place of the apphost; App Store build number. createdump (.NET's crash-dump
#    helper) is dropped: App Review rejects any executable without the sandbox entitlement
#    (ITMS-90296, measured on the first upload) and nothing in the app starts it.
rm -f "$app/Contents/MacOS/createdump"
sh "$macos/launcher/build.sh" "$app/Contents/MacOS/AgentZeroLite" >/dev/null
/usr/libexec/PlistBuddy -c "Set :CFBundleVersion $build" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Delete :LSApplicationCategoryType" "$app/Contents/Info.plist" 2>/dev/null || true
/usr/libexec/PlistBuddy -c "Add :LSApplicationCategoryType string public.app-category.developer-tools" "$app/Contents/Info.plist"
/usr/libexec/PlistBuddy -c "Add :ITSAppUsesNonExemptEncryption bool false" "$app/Contents/Info.plist" 2>/dev/null || true

ent="$here/entitlements.plist"
xattr -cr "$app"

if [ "${MAS_SANDBOX_TEST:-}" = "1" ]; then
  # The identifiers are restricted entitlements: without the profile that grants them the
  # process would be killed at launch, so the local test signs without them.
  test_ent="$out/sandbox-test.entitlements"
  cp "$ent" "$test_ent"
  /usr/libexec/PlistBuddy -c "Delete :com.apple.application-identifier" -c "Delete :com.apple.developer.team-identifier" "$test_ent"
  find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/AgentZeroLite" -exec codesign --force --sign - {} \;
  codesign --force --sign - --entitlements "$test_ent" "$app"
  codesign --verify --strict "$app"
  echo "[appstore] sandbox test bundle (ad-hoc): $app"
  exit 0
fi

: "${MAS_SIGN_ID:?set MAS_SIGN_ID}" "${MAS_PKG_ID:?set MAS_PKG_ID}" "${MAS_PROFILE:?set MAS_PROFILE}"
cp "$MAS_PROFILE" "$app/Contents/embedded.provisionprofile"

# 3) Inside-out: every nested file, then the bundle with the entitlements.
find "$app/Contents/MacOS" -type f ! -path "$app/Contents/MacOS/AgentZeroLite" | while IFS= read -r f; do
  if file -b "$f" | grep -q 'Mach-O.*executable'; then
    codesign --force --timestamp --sign "$MAS_SIGN_ID" --entitlements "$here/inherit.entitlements" "$f"
  else
    codesign --force --timestamp --sign "$MAS_SIGN_ID" "$f"
  fi
done
codesign --force --timestamp --sign "$MAS_SIGN_ID" --entitlements "$ent" "$app"
codesign --verify --deep --strict --verbose=2 "$app"

# 4) The upload package: productbuild with the installer identity, installing into /Applications.
pkg="$out/AgentZeroLite-$version-$build-appstore.pkg"
productbuild --component "$app" /Applications --sign "$MAS_PKG_ID" "$pkg"
echo "[appstore] $pkg ($version build $build)"
