# Windows acceptance driver

This console application drives real mouse and keyboard input on an idle, unlocked Windows test desktop. It observes the app through its logs and UI Automation. Do not use the mouse or keyboard while a scenario runs.

Build with .NET 10 on Windows:

```powershell
dotnet build tests/DropHalo.Acceptance/DropHalo.Acceptance.csproj -c Release -r win-x64 -p:Platform=x64
```

Use `ARM64` and `win-arm64` for ARM64. A framework-dependent driver needs the Windows Desktop runtime; an isolated SDK can be selected through the process-local `DOTNET_ROOT` and `PATH`.

```powershell
DropHalo.Acceptance.exe source
DropHalo.Acceptance.exe app --exe C:\path\DropHalo.exe --explorer
DropHalo.Acceptance.exe archives --exe C:\path\DropHalo.exe --iterations 30
DropHalo.Acceptance.exe performance --exe C:\path\DropHalo.exe --iterations 100
```

`source` verifies that a window covering Explorer receives no mouse press. `archives` repeats image conversion and undo, cancellation, edge placement, subtitle conversion, and ZIP repacking using fresh files. `performance` also accepts `--reopen-explorer` and `--grouped`.

Every first-frame sample must satisfy the original **show time + first-frame time <= 150 ms** limit. Modifier taps retain their **300 ms** contract, and a file must enter within **2 seconds**. Failed, missing, and slow samples remain failures. A source-window precondition failure occurs before the mouse is pressed; it is reported as a failure, never skipped or retried as an already-started drag.

The full `app` scenario uses fixed temporary test directories, the app's real log directory, and a sample folder in Pictures. Preserve existing contents before running it and restore them afterward. The archive and performance loops use unique profiles and directories. Their output and any failure screenshots are written under `%LOCALAPPDATA%\DropHaloAcceptance`.

## Remote execution

An SSH session has no interactive desktop. Use `spikes/run-in-console-session.ps1` to run a guest script in the logged-on user's desktop:

```powershell
.\spikes\run-in-console-session.ps1 -UserId <logged-on-user> `
    -TaskName DropHaloAcceptance-UniqueRun `
    -OutFile C:\Users\<user>\AppData\Local\Temp\drophalo-acceptance.log `
    -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\temp\run-acceptance.ps1'
```

When using Parallels, execute this helper and read progress from the default SYSTEM channel, without `prlctl exec --current-user`. The explicit `UserId` keeps the test itself in the ordinary user's interactive session. Interactive progress commands can open a terminal over Explorer: controlled ZIP tests reproduced three missing-file failures in 20 attempts this way, while the same binaries completed all 20 attempts without the interference. The driver now checks the foreground window and actual pointer hit immediately before pressing the mouse.

Keep the helper's nonzero exit code through any outer script with `exit $LASTEXITCODE`. The helper also fails if a task produces no output file and returns 124 on timeout.
