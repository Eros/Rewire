#!/usr/bin/env bash
# Builds Rewire.app for one Mac architecture, signs it, and wraps it in a DMG.
#
#   packaging/macos/package.sh [arm64|x64]
#
# Without signing settings the app is signed ad hoc, which runs on this Mac only.
# For a release, set:
#   REWIRE_SIGN_IDENTITY   the "Developer ID Application: ..." identity in your keychain
#   REWIRE_NOTARY_PROFILE  a profile saved with `xcrun notarytool store-credentials`
#   REWIRE_BUNDLE_ID       optional; defaults to com.georgemarr.rewire
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
arch="${1:-$(uname -m)}"
case "$arch" in
    arm64) rid="osx-arm64" ;;
    x64 | x86_64) rid="osx-x64"; arch="x64" ;;
    *) echo "Unknown architecture: $arch" >&2; exit 1 ;;
esac

version="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$root/Directory.Build.props")"
bundle_id="${REWIRE_BUNDLE_ID:-com.georgemarr.rewire}"
identity="${REWIRE_SIGN_IDENTITY:--}"
artifacts="$root/artifacts/macos-$arch"

# The bundle is built outside the repository: folders synced by iCloud Drive or other file providers
# attach attributes that cannot be removed and that codesign rejects.
out="$(mktemp -d "${TMPDIR:-/tmp}/rewire-package.XXXXXX")"
trap 'rm -rf "$out"' EXIT
app="$out/Rewire.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"

echo "Publishing Rewire $version for $rid"
dotnet publish "$root/src/Rewire.App/Rewire.App.csproj" -c Release -r "$rid" --self-contained \
    -p:UseAppHost=true -o "$out/publish" -v quiet -nologo

cp -R "$out/publish/." "$app/Contents/MacOS/"
cp "$root/packaging/macos/Rewire.icns" "$app/Contents/Resources/"
sed -e "s/\$(VERSION)/$version/g" -e "s/\$(BUNDLE_ID)/$bundle_id/g" \
    "$root/packaging/macos/Info.plist" > "$app/Contents/Info.plist"

xattr -cr "$app"

# Sign inside out: every other file first, then the executable with its entitlements, then the bundle.
# codesign treats everything in Contents/MacOS as code, so the .NET assemblies and data files need
# signatures too, as Avalonia's macOS packaging guide describes.
sign() {
    local options=(--force --sign "$identity" --options runtime)
    if [[ "$identity" != "-" ]]; then
        options+=(--timestamp)
    fi
    codesign "${options[@]}" "$@" 2> >(grep -v "replacing existing signature" >&2)
}

echo "Signing with ${identity/#-/an ad hoc signature}"
while IFS= read -r -d '' file; do
    sign "$file"
done < <(find "$app/Contents/MacOS" -type f ! -name Rewire -print0)
sign --entitlements "$root/packaging/macos/Rewire.entitlements" "$app/Contents/MacOS/Rewire"
sign --entitlements "$root/packaging/macos/Rewire.entitlements" "$app"
codesign --verify --strict --deep "$app"

notarise() {
    if [[ "$identity" == "-" || -z "${REWIRE_NOTARY_PROFILE:-}" ]]; then
        echo "Skipping notarisation of $(basename "$1"): no signing identity or notary profile"
        return
    fi
    echo "Notarising $(basename "$1")"
    local submission="$1"
    if [[ -d "$1" ]]; then
        submission="$out/$(basename "$1").zip"
        ditto -c -k --keepParent "$1" "$submission"
    fi
    xcrun notarytool submit "$submission" --keychain-profile "$REWIRE_NOTARY_PROFILE" --wait
    xcrun stapler staple "$1"
}

notarise "$app"

echo "Creating the disk image"
staging="$out/dmg"
mkdir -p "$staging"
cp -R "$app" "$staging/"
ln -s /Applications "$staging/Applications"
dmg="$out/Rewire-$version-macos-$arch.dmg"
hdiutil create -volname "Rewire" -srcfolder "$staging" -ov -format UDZO "$dmg" -quiet
rm -rf "$staging"

if [[ "$identity" != "-" ]]; then
    codesign --force --sign "$identity" --timestamp "$dmg"
fi
notarise "$dmg"

rm -rf "$artifacts"
mkdir -p "$artifacts"
mv "$dmg" "$artifacts/"
echo "Built $artifacts/$(basename "$dmg")"
