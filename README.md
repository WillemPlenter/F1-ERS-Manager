# F1 ERS Manager

made by Willem Plenter (SkaffaWilly)

![F1 ERS Manager logo](source/F1%20ERS%20Manager%20Logo.png)

F1 ERS Manager is a standalone Windows utility for controlling the ERS battery
level of the player's two cars in F1 Manager 2023 and F1 Manager 2024.

**Version:** 1.2 · **Platform:** Windows x64 · **License:** MIT

One executable supports both games. It detects the running title, validates the
complete game executable, finds both player cars, and enables controls only when
the current race data passes all safety checks. Cheat Engine and external
runtimes are not required.

## Download and setup

1. Download `F1-ERS-Manager-v1.2-win-x64.zip`.
2. Verify the optional `.zip.sha256` file as described under
   [Download integrity](#download-integrity).
3. Extract the ZIP into a writable folder outside the game's installation.
4. Start `F1 ERS Manager.exe` and either supported game, in either order.
5. Wait for **CONNECTED** and confirm the detected game.

Only `F1 ERS Manager.exe` is required to run the tool. The source and
technical documentation are included so the release can be inspected and
rebuilt.

Keep only one supported game running. If F1M23 and F1M24 are both active, ERS
actions stay disabled until one game is closed.

## Controls

| Default shortcut | Action |
| --- | --- |
| F8 | Hold the current ERS level, or release the selected active hold |
| F9 | Set the selected ERS battery to 100% once |
| F10 | Drain the selected ERS battery to 0% once |
| F11 | Show or hide the in-game overlay |
| Ctrl+F12 | Cycle Car 1 → Car 2 → Both cars → Car 1 |

Choose **Car 1**, **Car 2**, or **Both cars** under **Target**. Both cars is the
default. The overlay shows the active target on its right side, together with
the configured target-cycle shortcut.

The defaults are separate from F1 Speed Manager's F1–F7 controls, so both tools
can run at the same time. Plain F12 is commonly reserved by Steam; Ctrl+F12 is
used to avoid that conflict.

Global shortcuts respond only while F1 ERS Manager or the exact connected game
is in the foreground. Cycling the target and toggling the overlay are display
actions and never write ERS memory.

## Custom hotkeys

Select **Hotkeys…**, click a field, and press the desired key or combination.
Each action needs a unique shortcut. A regular letter or navigation key requires
Ctrl, Alt, or Shift; F1–F24 can be used directly if Windows has not reserved
them.

Settings are stored in `F1 ERS Manager.hotkeys.ini` beside the
executable. Older three-action and four-action settings files migrate
automatically without overwriting an existing custom F11 or Ctrl+F12 binding.
Delete or rename an invalid settings file to restore defaults.

Personal hotkeys and logs are generated locally and are not included in the
download or source repository.

## In-game overlay

The overlay starts off. Enable it with the **Overlay** button or F11.

The 300×48 badge shows:

- the latest validated ERS percentage for both player cars;
- a **HOLD** marker for each battery currently being held;
- the target used by the next F8, F9, or F10 action;
- the configured target-cycle shortcut.

The default **Left HUD gap** position sits below the standings and above the
left driver panel. Five alternatives are available: top-left, top-center,
top-right, bottom-left, and bottom-right. F1 Speed Manager defaults to top-right,
so the two overlays start in separate areas.

The overlay is a separate topmost, click-through window. It never takes focus
and does not read or write game memory itself. It appears only over the verified
foreground game while fresh values for both cars are available. It hides on
Alt-Tab, minimization, disconnection, stale data, unsupported builds, or errors.

Use windowed or borderless windowed mode. True exclusive fullscreen can cover
external desktop overlays.

## Supported games

| Game | Supported Steam build | Verified executable SHA-256 |
| --- | --- | --- |
| F1 Manager 2023 | 16843164 | `d6e8f3ba892d65e947836f90e81ad590fef4720f6a2e6641832482427b8afc36` |
| F1 Manager 2024 | 17356935 / update 1.11 | `1198feb7b1f39653fe51f04c9b0acb4d125a67d0e8ba6bda1fa353b3368ab356` |

Every other executable hash is refused. Game updates usually change the
executable and therefore require a compatibility review and a new release.
There is no option to bypass this check.

## How the ERS actions work

Before every write, the controller rechecks the supported build, live process,
race objects, car identity, telemetry range, and battery address. It writes only
the four-byte floating-point ERS battery field for the selected car or cars.

- **Hold current ERS** reads the current validated value and reapplies it every
  75 ms while that car remains valid.
- **Set to 100%** performs one validated write and does not hold the value.
- **Drain to 0%** performs one validated write and does not hold the value.
- A one-time action stops an existing hold only for the selected car or cars.
- Closing the application stops every hold loop before the process handle is
  released.

F1M24 also reads the current save in read-only mode. The player team and its two
currently contracted driver IDs must match exactly with the live race data. An
autosave created before the current game process started is refused; let the
game save again before retrying.

See [MECHANISM.md](MECHANISM.md) for the validation and memory-write design.

## Compatibility and safety

- Windows 10 or 11 x64 with .NET Framework 4.8 or later.
- One supported F1 Manager process at a time.
- The tool requests only normal user privileges; run the game and tool at the
  same privilege level.
- No game files or save files are modified.
- No graphics hooks, injected DLLs, remote threads, or anti-cheat bypasses are
  used.
- The executable is unsigned. Windows SmartScreen or antivirus software may
  ask for confirmation because the tool opens another process and writes a
  validated gameplay field. Verify the hashes and source instead of disabling
  security protection.

Memory tools can affect game stability. Save your career before using a new
release and test controls in a disposable session first.

## Troubleshooting

- **Game not detected:** start the actual game, not only its launcher.
- **Unsupported build or hash:** this release does not match that executable.
- **Open or resume a race:** both player cars are not currently available.
- **Both games are running:** close either F1M23 or F1M24.
- **Hotkey already in use:** change that shortcut or close the application
  reserving it.
- **F1M24 save is stale:** allow the current session to create a new autosave.
- **Overlay is hidden:** bring the verified game to the foreground and use
  windowed or borderless mode.
- **Windows error 5:** run the tool and game at the same privilege level.

`F1 ERS Manager.log` is written beside the executable for
diagnostics. It can contain local installation paths; review it before sharing.
Logs and hotkey files are excluded from the GitHub release.

## Building from source

Run:

```bat
source\\build.cmd
```

The script uses the built-in 64-bit .NET Framework compiler, creates
`F1 ERS Manager.exe` in the project root, builds a temporary console
test executable, runs the isolated self-tests, and removes that test
executable. No NuGet packages or network downloads are required.

The final v1.2 source passes 67 checks covering hotkeys and migration, target
cycling, overlay gating and positioning, exact-build helpers, ERS value ranges,
the F1M24 save reader, and fail-closed multi-game selection. The self-tests do
not attach to a game or write game memory.

The supplied logo is stored unchanged as
`source/F1 ERS Manager Logo.png`. `source/app.ico` is
embedded into the executable as its Windows icon.

## Release files

- `F1 ERS Manager.exe` — standalone Windows x64 application.
- `README.md` — setup, controls, compatibility, and troubleshooting.
- `MECHANISM.md` — technical validation and write-safety design.
- `LICENSE` — MIT license for the software.
- `SHA256SUMS.txt` — SHA-256 hashes for every packaged file.
- `source/` — complete buildable source and embedded visual assets.

## Download integrity

The GitHub release includes
`F1-ERS-Manager-v1.2-win-x64.zip.sha256`. Verify the ZIP in
PowerShell:

```powershell
Get-FileHash -LiteralPath '.\\F1-ERS-Manager-v1.2-win-x64.zip' -Algorithm SHA256
```

Compare the result with the hash in the `.sha256` file.
`SHA256SUMS.txt` inside the ZIP lists every packaged file. Hashes
detect changed files; they are not a digital signature.

## License

Copyright (c) 2026 Willem Plenter (SkaffaWilly). Released under the
[MIT License](LICENSE).

---

Unofficial utility. Not affiliated with Frontier Developments, Formula 1,
F1 Manager, Steam, or their respective owners. Product names and trademarks
belong to their owners.
