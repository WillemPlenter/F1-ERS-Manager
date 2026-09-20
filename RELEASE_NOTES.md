# F1 ERS Manager 1.2.1

Suggested GitHub tag: `v1.2.1`

## Release highlights

- Uses the revised F1 ERS Manager artwork throughout the README, Windows icon,
  executable, title bar, and taskbar.
- Supports the verified Steam builds of F1 Manager 2023 and F1 Manager 2024.
- Holds the current ERS level independently for Car 1, Car 2, or both cars.
- Provides one-time 100% and 0% ERS actions.
- Adds a click-through in-game overlay with both ERS percentages and hold state.
- Uses **Left HUD gap** as the default overlay placement, below the standings.
- Shows the active target and configurable **Ctrl+F12** cycle shortcut on the
  right side of the overlay.
- Uses separate F8–F11 and Ctrl+F12 defaults, allowing simultaneous use with
  F1 Speed Manager's F1–F7 defaults.
- Provides an English interface, English diagnostics, configurable hotkeys, and
  automatic migration of older settings.
- Rejects unknown game builds and ambiguous player-car matches.

## Supported builds

- F1 Manager 2023 — Steam build 16843164
- F1 Manager 2024 — Steam build 17356935 / update 1.11

## Validation

- Release build: Windows x64, .NET Framework 4.8
- Automated self-tests: 67 passed
- F1M23 exact-build connection and overlay behavior verified locally
- Ctrl+F12 target cycling verified without an ERS write
- F1M24 save/team/driver parsing verified with the included synthetic fixture

## GitHub assets

Upload both files:

- `F1-ERS-Manager-v1.2.1-win-x64.zip`
- `F1-ERS-Manager-v1.2.1-win-x64.zip.sha256`

The ZIP contains the executable, complete source, license, documentation, and
per-file SHA-256 manifest. It contains no logs or personal hotkey settings.

## Known limitations

- Only the exact executable hashes documented in `README.md` are
  supported.
- F1M24 requires a current save whose team and drivers match the live race.
- True exclusive fullscreen can cover the external overlay; use borderless or
  windowed mode.
- The executable is unsigned.

Unofficial utility. Not affiliated with Frontier Developments, Formula 1,
F1 Manager, Steam, or their respective owners.
