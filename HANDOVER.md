# GUI Phone Browser handover

## Purpose

A small Windows app for browsing and transferring files between a PC and Android phone over ADB. Keep it to one WPF project with a clear dual-pane UI.

## Build and portability

The project targets a self-contained `win-x64` single-file executable. Build with:

```powershell
dotnet publish GuiPhoneBrowser\GuiPhoneBrowser.csproj -c Release -o publish
```

ADB and its Windows USB libraries are embedded in the executable and extracted under the user's Local AppData on first use. The Android platform-tools `NOTICE.txt` is embedded too. Share `publish\GuiPhoneBrowser.exe`; users do not need .NET or Android Studio installed.

## Transfer behavior and limits

- Phone to PC uses `adb pull -a` to preserve modified time and mode. Do not overwrite that timestamp using a parsed `ls` date; it loses timezone and precision information.
- PC to phone sets the source modified time using an explicit UTC timestamp, then checks the phone's reported time. Android storage may limit timestamp precision or refuse the change; the app reports this.
- The app preserves modified time only. This ADB flow does not provide a portable creation time.
- File moves require a known matching byte size. PC-to-phone moves also require the modified time check to succeed.
- Folder moves are disabled until the app can verify every nested item. Folder copies may proceed, but are not a full integrity audit.
- A move must never delete a source if transfer verification fails or is unavailable.

## Existing self-check

Launching with `--test` runs a small built-in smoke check for local file helpers and ADB availability. It does not test a real phone transfer, timestamp preservation on device storage, or folder integrity. Do not describe it as a comprehensive automated test suite.

## Before claiming release readiness

Build the Release executable and confirm the publish output contains the single `.exe`. Manually check browsing, copy, overwrite/skip/cancel behavior, and modified times in both directions on a real Android device. Device, Android version, and storage filesystem can affect results.
