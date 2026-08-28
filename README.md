# Forza Horizon Radio Extender (FHRE)

**Add your own music to the built-in radio stations of Forza Horizon.**

Forza Horizon Radio Extender is a Windows desktop application for adding custom music to the existing in-game radio stations.

Your tracks can be added **alongside the original music**, while existing tracks can also be **replaced without being limited to their original duration** or disabled entirely.

FHRE supports:

- **Forza Horizon 4**
- **Forza Horizon 5**
- **Forza Horizon 6**

## Download

The latest release is available on Nexus Mods:

**[Download Forza Horizon Radio Extender on Nexus Mods](https://www.nexusmods.com/forzahorizon6/mods/416?tab=files)**

## Features

- Add your own music to existing Forza Horizon radio stations.
- Keep custom tracks alongside the original soundtrack.
- Replace original tracks without their original duration limitation.
- Disable unwanted original tracks.
- Built-in editor for Forza Horizon timing markers.
- Preview tracks and loops directly in the editor.
- Automatic search for potential seamless loop regions.
- Automatic backup of original game files.
- Manual backups for individual stations.

## How to add your own music to Forza Horizon

Using FHRE is straightforward:

1. Launch the application.
2. Select the game installation folder if required.
3. Select a radio station.
4. Add your music tracks.
5. Adjust the track markers if desired.
6. Click **Build** and wait for the process to finish.
7. Start the game.

Your custom music is now part of the selected in-game radio station.

You can also replace original tracks with your own music or disable original tracks completely.

FHRE automatically creates a backup of the affected original game files before they are modified.

# Limitations

Before using FHRE, keep the following limitations in mind.

### Use original game sound banks

FHRE depends on the exact structure of the original game's sound bank headers.

Other radio replacement tools may modify this structure and prevent FHRE from reading or rebuilding the bank correctly.

If another track replacement tool has previously modified the game, restore the original files first—for example by verifying or repairing the game installation.

### 2 GB bank limit

There is a hard **2 GB limit per sound bank**.

A radio bank larger than 2 GB will not work correctly in-game.

### Close the game before building

Forza Horizon must be closed while FHRE modifies or rebuilds the radio files.

The game uses the same files and can interfere with the build process.

### Finish the prologue

Custom music may not play correctly until the game's introductory/prologue section has been completed.

### Other issues

Other bugs and compatibility issues may exist.

Always keep backups of important files.

# Third-party tools

FHRE uses several external tools.

They are **not included in this repository** and must be obtained separately from their respective authors.

| Tool | Purpose | Folder |
|---|---|---|
| **ffmpeg** | decoding and normalizing the source audio | `ffmpeg\` |
| **FMOD FSBank CLI** (`fsbankcl`) | packing audio into FSB5 banks | `fsbank\` |
| **vgmstream** | decoding game audio for preview | `vgmstream\` |

Place the required tools in their corresponding subdirectories next to `fhre.exe`.

You are responsible for obtaining and using these tools in accordance with their respective licenses.

FMOD in particular is distributed under the FMOD license. Review its license terms before use.

# Build and run

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

# Disclaimer

This project is not affiliated with or endorsed by Microsoft, Playground Games, Turn 10 Studios,
or Firelight Technologies (FMOD). "Forza Horizon" and FMOD are trademarks of their respective
owners.

The application modifies the files of an installed game. You use it **at your own risk**: the
author is not responsible for corrupted game data, lost progress, bans from online services, or
any other consequences. Always keep backups; the restore feature is provided "as is", without
any warranty.

Only use this tool with audio you have the rights to. Responsibility for complying with the
copyright of the added music and with the licenses of the third-party tools rests with the user.
