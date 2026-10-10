# GUI Phone Browser

A small Windows app for browsing and transferring files between a PC and an Android phone over ADB.

## What it does

- Browse Windows folders and Android shared storage in a dual-pane interface.
- Copy files in either direction and move individual files after verification.
- Preserve modified times where the device and file system support it.

Folder moves are disabled until the app can verify every nested item. Android creation time is not portable through this ADB transfer flow.

## Run or build

The Release publish is a self-contained `win-x64` executable. The project embeds the required ADB binaries and extracts them under the current user's Local AppData at runtime.

Build from the repository root with the .NET 8 SDK:

```powershell
dotnet publish GuiPhoneBrowser\GuiPhoneBrowser.csproj -c Release -o publish
```

The executable is written to `publish\GuiPhoneBrowser.exe`. Connect an Android phone over USB, enable USB debugging, and approve the computer on the phone.

## Timestamp details

- Phone-to-PC downloads use `adb pull -a` to preserve modified time and mode.
- PC-to-phone transfers set modified time in UTC and check the phone's reported time; Android storage may limit precision or reject timestamp changes.
- The app preserves modified time, not creation time.

`--test` runs basic local and ADB smoke checks. It does not test transfer behavior on a real phone.

See [HANDOVER.md](HANDOVER.md) for implementation and verification notes.
