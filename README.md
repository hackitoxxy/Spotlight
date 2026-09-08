# Spotlight / Moonlight level editor

This repository builds Spotlight for Super Mario 3D World and Moonlight for Super Mario Odyssey.
Both configurations produce an executable named `Spotlight.exe`. Select a **Moonlight** build
configuration for Odyssey; it enables the `ODYSSEY` code paths.

## System Requirements

- OpenGL 3.0+
- Windows 8+

## Assistance and How to Use

A full guide to Spotlight can be found on the [Spotlight Wiki](https://github.com/jupahe64/Spotlight/wiki).

## Compiling from Source

### Prerequisites

- Windows and Visual Studio with the **.NET desktop development** workload (or the corresponding
  Visual Studio Build Tools). Install the **.NET Framework 4.8 SDK and targeting pack**; the runtime alone cannot compile the editor.
- A recent .NET SDK/C# compiler. Application source uses C# 12 features, so use Visual Studio 2022
  17.8+ with .NET SDK 8+, or newer. The new test project targets **.NET 10** and requires the .NET 10 SDK/runtime
  (Visual Studio 2026 for GUI test execution). The application itself still targets .NET Framework 4.8;
  `FileFormats3DW` targets .NET Standard 2.0.
- Git and the sibling [GL_EditorFramework](https://github.com/hackitoxxy/GL_EditorFramework) repository.
  The solution expects these exact relative paths:

  ```text
  parent/
    Spotlight/
      Spotlight.sln
      SpotLight/SpotLight.csproj
      FileFormats3DW/FileFormats3DW.csproj
    GL_EditorFramework/
      Gl_EditorFramework/GL_EditorFramework.csproj
  ```

- Keep the checked-in `DLLs/`, `SpotLight/DLLs/`, and `FileFormats3DW/nativelib/` files. They supply
  BFRES/BNTX, archive/editor support, and the x86/x64 native Yaz0 compression library.
- NuGet access for the first restore. The legacy projects use `packages.config` and the solution's
  `packages/` directory; the SDK project uses the global NuGet cache. `System.Resources.Extensions`
  8.0.0 is restored by `FileFormats3DW` because the legacy WinForms resource builds also reference it.
  The verified sibling revision is `f95c5a9` of `hackitoxxy/GL_EditorFramework` (based on Kirbymimi's fork).
  Some other framework revisions have different APIs/dependency paths; retain a known-compatible checkout.
  The build script does not pull or upgrade it automatically.

Game files are not needed to compile or run unit tests. Running the editor requires a game dump with
`StageData` and `ObjectData`, and OpenGL 3.0+ drivers. Moon-name editing also requires
`LocalizedData/<language>/MessageData/StageMessage.szs`. Project Directory is optional: when blank,
moon names are read and saved directly in Game Directory, matching an extracted-ROM editing workflow.

### Command line: isolated build (recommended while an editor is running)

From the repository directory, in PowerShell or Command Prompt:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1 -Configuration "Debug Moonlight" -Restore
```

The execution-policy option applies only to that PowerShell process. `Build.ps1` finds 64-bit Visual
Studio MSBuild with `vswhere`, restores dependencies, and redirects **every project's output and
intermediate files**, including GL_EditorFramework, under:

```text
artifacts/Debug-Moonlight/bin/Spotlight/Spotlight.exe
artifacts/Debug-Moonlight/obj/<project>/
```

This does not replace `SpotLight/bin/Moonlight_DEBUG/Spotlight.exe` or its dependencies. Do not use
the same artifacts configuration to rebuild an executable you have subsequently launched there.
Pass `-BuildRoot .\artifacts\another-build` to create another isolated build while that one runs.
Omit `-Restore` on subsequent builds unless dependencies changed. Other supported configurations
are `Release Moonlight`, `Debug Spotlight`, and `Release Spotlight`. Pass `-MSBuildPath` if Visual
Studio is installed somewhere `vswhere` cannot discover.

Launch the isolated application from its output directory (native libraries use relative paths):

```powershell
Set-Location .\artifacts\Debug-Moonlight\bin\Spotlight
.\Spotlight.exe
```

Keep the entire output directory, including `lib`, `nativelib`, `Shaders`, and `Splash`; copying only
the EXE is insufficient. A separately located EXE may have separate per-user settings and ask for
game/project paths again.

For a conventional build from a **Developer PowerShell for Visual Studio**, use:

```powershell
msbuild Spotlight.sln /t:Restore /p:RestorePackagesConfig=true /p:Configuration="Debug Moonlight" /p:Platform="Any CPU"
msbuild Spotlight.sln /t:Build /m /p:Configuration="Debug Moonlight" /p:Platform="Any CPU"
```

This conventional build writes to `SpotLight/bin/Moonlight_DEBUG/`; close any application running
from that directory first. Use **Any CPU**: several inherited x86/x64 solution mappings do not
correspond to configured project platforms. Prefer `Build.ps1` over the older `Build.py`, which
also pulls a dependency repository and expects Python, NuGet CLI, and 7-Zip on PATH.

### Visual Studio GUI

1. Install the prerequisites and arrange the sibling repositories as above.
2. Open `Spotlight.sln`. Restore NuGet packages; running the isolated command with `-Restore` once
   also prepares dependencies if automatic restore is disabled.
3. In **Build → Configuration Manager**, select **Debug Moonlight** (Odyssey) or **Debug Spotlight**
   (3D World) and **Any CPU**. Set Spotlight as the startup project.
4. Use **Build → Build Solution**, then **Debug → Start Without Debugging** (Ctrl+F5), or F5 to debug.
   Default outputs are `SpotLight/bin/Moonlight_DEBUG` and `SpotLight/bin/Spotlight_DEBUG`.

For an isolated build from the GUI while the previous executable is running, add **Tools → External
Tools → Add** with command `powershell.exe`, initial directory `$(SolutionDir)`, and arguments:

```text
-NoProfile -ExecutionPolicy Bypass -File "$(SolutionDir)Build.ps1" -Configuration "Debug Moonlight" -Restore
```

Enable **Use Output window**. Invoke that tool to build into `artifacts`; ordinary F5/Build Solution
still uses the normal output folders.

### Unit tests

The MSBT feature introduces `tests/Spotlight.MessageTests`, using MSTest. It compiles the exact
production reader/writer and document sources directly, so tests need no game files, OpenGL,
native Yaz0 DLL, or running editor. Its only extra packages are test tooling, not editor dependencies.

```powershell
dotnet test tests/Spotlight.MessageTests/Spotlight.MessageTests.csproj --verbosity minimal
```

If `dotnet` is not on PATH, use `& "C:\Program Files\dotnet\dotnet.exe" test ...` in PowerShell.
For GUI execution, open `Spotlight.Tests.sln` in Visual Studio 2026, then **Test → Test Explorer →
Run All Tests**. This separate solution avoids rebuilding the running application.

Coverage includes byte-identical no-edit round trips, endian/encoding combinations, Unicode,
label hashes, metadata insertion, malformed input, preservation of unrelated/tagged messages,
undo/redo snapshots, project overrides, archive merging, backups, and conflicting external edits.
Stage-change regression tests also cover own-save recognition, timestamp-independent external
changes, partial save failures, and reload-dialog reentrancy. Spotlight acknowledges each stage
archive immediately after writing it; its own saves do not trigger the external-modification prompt.
Title persistence tests cover replacing a saved first letter with the full name for both existing
and newly inserted entries. Save, Save As, and tab closing commit the active property textbox
before saving or checking for unsaved changes.
Document tests use a fake container to isolate saving behavior; passing them does not establish
in-game compatibility or exercise native compression. Before distributing a mod, verify a new
title in-game using the same language and game version as the edited files.

For a manual UI regression check, save `B` as a moon name, then replace it with `Ben farted`.
While the caret is still in Moon Name, press Ctrl+S without pressing Enter or clicking elsewhere.
Close and reopen the stage; the full name should remain. Also check Ctrl+Shift+S (Save As) and
closing a tab with an uncommitted title: the close action should recognize the unsaved edit.

If Save reports a file in use, stop emulation before retrying, keeping Spotlight open so its
pending edits remain available. Ryujinx can hold both the stage and message archives open in
the mod's romfs. Do not force-unlock the files. Save errors identify the archive (and the backup
path for message saves). Regression tests check that locked saves preserve data and can be retried.
For Windows file-use diagnostics (does not stop applications or modify game files):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Get-ArchiveUsers.ps1 -ArchivePath "C:\your-romfs\StageData\WaterfallWorldHomeStageMap.szs"
```

This uses Windows Restart Manager; if its query is denied, try an elevated PowerShell window.
An empty result cannot rule out a transient lock.

Moon-name saves now use backed-up overwrite by default; there is no checkbox to enable.
Like stage saves, this preserves the existing file identity instead of replacing the file.
Ryujinx may remain open if its retained handles permit writes. The save opens without truncation,
checks for concurrent edits, writes and
flushes a verified `.bak`, then overwrites, adjusts the length, flushes and verifies the archive.
It cannot bypass write locks and is **not atomic**: a crash or failed write may require restoring
the `.bak` with the emulator closed. Existing readers can see an incomplete archive during the
write; stopping emulation is safest. A user verified saving with Ryujinx running and seeing the
updated moon name after restarting emulation, but live reload is not guaranteed. Project output
and conflict checks still apply. The document API retains `Save(overwriteInPlace: false)` for
callers that explicitly want file replacement instead.

To probe the required access without writing any archive bytes (leave Ryujinx open after stopping
the game):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Test-ArchiveWriteAccess.ps1 -ArchivePath "C:\your-romfs\LocalizedData\USen\MessageData\StageMessage.szs"
```

Tests cover a retained read handle that blocks replacement but allows overwrite, longer/shorter
archives, locked destinations/backups, new project output, and stale-member conflicts.

An optional integration check uses the isolated Debug Moonlight build and a supplied local archive:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tests\Verify-MessageArchive.ps1 -ArchivePath "C:\your-romfs\LocalizedData\USen\MessageData\StageMessage.szs"
```

This reads every MSBT, inserts a test title into `Special2WorldHomeStage.msbt` (override with
`-StageName`), and verifies all members after native SARC/Yaz0 repacking **in memory**. It never saves
the modified archive or launches the editor. On the development machine, Visual Studio 2026 MSBuild
and .NET SDK 10.0.400 were used; a local USen archive passed all 201 MSBT no-edit round trips and the
title-insertion archive round trip. This is file-format validation, not an in-game test.

## Editing Odyssey moon names

In a Moonlight build, select a single object and expand **Objects → Selected → Edit Moon Name**.
Choose a game language and edit **Moon Name**; press Enter or finish editing to commit it to Undo.
Save the stage to write message edits. The field is available for all general objects because
treasure chests and other actors can produce moons; giving an arbitrary object a title does not
make it produce a moon. There is deliberately no actor-name substring filter: it would miss
some producers and include unrelated actors. Requiring an existing message would also hide
new producers whose titles have not been created yet.

- Lookup: `StageMessage.szs` → `<owning-stage-name>.msbt` → `ScenarioName_<Object ID>`.
  The displayed message key is informational. `ScenarioName_` is a literal prefix.
- Missing labels are created when a nonempty name is entered. Existing moon metadata provides
  the template. `UnitConfig.DisplayName` remains a separate stage property.
- Edits affect only the chosen language. If **File → Options → Project Directory** is configured,
  its archive takes precedence over Game Directory regardless of timestamps, and saves go there.
  Otherwise, the archive in **Game Directory** is edited directly. No separate project setup is
  required to load or edit names. Saves preserve other archive members and retain a `.bak` of the
  previous destination archive in either workflow. Undo/Redo restores complete message data,
  including entry creation. Hover over Moon Name to see the exact output path.
- Messages containing binary formatting tags are read-only in this plain-title editor. Missing
  stage MSBT files, unsupported attribute layouts, and unknown indexed sections show an error;
  use a full message editor for those cases. NPC dialogue editing is not included.
- Changing an Object ID or duplicating an object looks up the new ID; titles are **not automatically
  copied or renamed**, and deleting an object does not delete its message. Enter a title for the new ID.
  Save As copies loaded language documents to the new stage name; other languages are not migrated.
- Stage files and message archives are separate writes, not one filesystem transaction. Failed
  saves retain unsaved state and report that some stage files may already have been saved.

The format implementation and its high-level documentation are in `FileFormats3DW/MsbtFile.cs`;
archive/document ownership is in `FileFormats3DW/StageMessageDocument.cs`. Neither MSBT Editor
Reloaded nor MoonFlow is required as a dependency.

# Join Us
If you need help with the program or editing the game, you can Join the Cat Chat (<a href="https://discord.gg/9JGKSze"><img src="https://img.shields.io/discord/308323056592486420.svg?color=7289da&logo=discord&logoColor=white" alt="The Cat Chat" /></a>). You can talk to other SM3DW hackers here as well as show your own hacking accomplishments.<br/>(*Dislaimer: We cannot help you get the 3D World files*)

# Credits

- JuPaHe64: Project Leader, Lead Programmer, GL Editor Framework Programmer
- Super Hackio: Database Programmer, Debugger, Lead Tester, GitHub Manager

- Ray Koopa: Syroot Library Developer
- KillzXGaming: Switch Toolbox Developer (Some code was borrowed), Rendering Assistant
- KFreon: DXT1 Decompression

- Whitehole (SMG Level editor): Some features and visuals were inspired by Whitehole
- Kirbymimi: Layer improvements, TryGetObjectList updates, and fixes for broken database generation.
