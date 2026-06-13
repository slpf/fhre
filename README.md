# Forza Horizon Radio Extender (FHRE)

A Windows desktop application that adds your own music to the radio stations of Forza Horizon 4 / 5 / 6.

**Third-party tools** (not included in the repository; placed in subfolders next to `fhre.exe`):

| Tool | Purpose | Folder |
|---|---|---|
| **ffmpeg** | decoding and normalizing the source audio | `ffmpeg\` |
| **FMOD FSBank CLI** (`fsbankcl`) | packing audio into FSB5 banks | `fsbank\` |
| **vgmstream** | decoding game audio for preview | `vgmstream\` |

You must obtain these tools yourself from their authors and use them in accordance with their
own licenses. FMOD in particular is distributed under the FMOD license — review its terms
before use.

## Build and run

Build the release `fhre.exe` and package it with the tools into an archive using the build.bat.

Or build manually:

```powershell
dotnet publish src\FH6RB.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -o bin\publish
```

On launch, point the application to the game's installation folder. It then scans that folder
for the game executable, the RadioInfo files, and the sound banks, lists the radio stations,
and lets you add tracks. A backup of the affected files is created before the first change.

## Disclaimer

This project is not affiliated with or endorsed by Microsoft, Playground Games, Turn 10 Studios,
or Firelight Technologies (FMOD). "Forza Horizon" and FMOD are trademarks of their respective
owners.

The application modifies the files of an installed game. You use it **at your own risk**: the
author is not responsible for corrupted game data, lost progress, bans from online services, or
any other consequences. Always keep backups; the restore feature is provided "as is", without
any warranty.

Only use this tool with audio you have the rights to. Responsibility for complying with the
copyright of the added music and with the licenses of the third-party tools rests with the user.
