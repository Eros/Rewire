# Packaging

Rewire ships as a signed and notarised app in a disk image on macOS, and as a signed Velopack installer on Windows. Both builds are self-contained, so users do not need .NET installed.

Everything is written to `artifacts/`, which git ignores.

## macOS

```bash
packaging/macos/package.sh arm64   # Apple silicon
packaging/macos/package.sh x64     # Intel
```

With no settings, the app is signed ad hoc. That build opens on the Mac that made it, but Gatekeeper blocks it anywhere else. For a release, sign it with a Developer ID and have Apple notarise it:

1. Install your **Developer ID Application** certificate in your keychain, and find its name with `security find-identity -v -p codesigning`.
2. Save notarisation credentials once, using an app-specific password from your Apple account:

   ```bash
   xcrun notarytool store-credentials rewire --apple-id you@example.com --team-id TEAMID
   ```

3. Build with both settings:

   ```bash
   REWIRE_SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" \
   REWIRE_NOTARY_PROFILE=rewire \
   packaging/macos/package.sh arm64
   ```

`REWIRE_BUNDLE_ID` overrides the bundle identifier, which defaults to `com.georgemarr.rewire`.

The script builds the bundle in a temporary folder rather than in the repository. Folders synced by iCloud Drive or another file provider attach attributes to files that `codesign` refuses to sign.

The first time Rewire opens the v-drive, macOS asks the user whether it may use removable volumes. The reason it shows comes from `NSRemovableVolumesUsageDescription` in `Info.plist`.

## Windows

```powershell
pwsh packaging/windows/package.ps1
```

This produces `Rewire-win-Setup.exe`, a portable zip and the Velopack update feed in `artifacts/windows/releases`. Unsigned installers trigger a SmartScreen warning. To sign, set either:

- `REWIRE_SIGN_PARAMS` to the arguments for signtool, such as `/f cert.pfx /p password /fd sha256 /tr http://timestamp.digicert.com /td sha256`, or
- `REWIRE_TRUSTED_SIGNING_FILE` to an Azure Trusted Signing `metadata.json`.

Velopack can also package for Windows from a Mac with `dotnet vpk "[win]" pack ...`, which is useful for checking the installer builds, but signing has to run on Windows.

## Releases

Pushing a tag such as `v0.1.0` runs `.github/workflows/release.yml`. It builds both Mac architectures and the Windows installer, then drafts a GitHub release with them for you to check and publish. It reads these repository secrets:

| Secret | What it holds |
| --- | --- |
| `MACOS_CERTIFICATE` | The Developer ID Application certificate and key, exported as .p12 and base64-encoded |
| `MACOS_CERTIFICATE_PASSWORD` | The .p12 export password |
| `MACOS_SIGN_IDENTITY` | The certificate's name, as `security find-identity` prints it |
| `APPLE_ID`, `APPLE_TEAM_ID`, `APPLE_APP_PASSWORD` | The account and app-specific password used to notarise |
| `WINDOWS_CERTIFICATE` | The code-signing certificate as a base64-encoded .pfx |
| `WINDOWS_CERTIFICATE_PASSWORD` | The .pfx password |

Bump `<Version>` in `Directory.Build.props` before tagging; every package reads its version from there.

The app icon is drawn from `packaging/icon/rewire.png`, from which `packaging/macos/Rewire.icns` and `src/Rewire.App/Assets/rewire.ico` were made.
