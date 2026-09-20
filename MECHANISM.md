# Technical overview

F1 ERS Manager is an external Windows Forms controller. It does not modify the
game executable, packaged assets, saves, renderer, or input code. Its only game
write is a four-byte ERS battery value after the corresponding process, build,
race object, car identity, telemetry, and address have been revalidated.

## Supported executables

One executable contains separate controllers for the two verified Steam builds.

| Property | F1 Manager 2023 | F1 Manager 2024 |
| --- | --- | --- |
| Steam build | 16843164 | 17356935 / update 1.11 |
| Process name | `F1Manager23` | `F1Manager24` |
| SHA-256 | `d6e8f3ba892d65e947836f90e81ad590fef4720f6a2e6641832482427b8afc36` | `1198feb7b1f39653fe51f04c9b0acb4d125a67d0e8ba6bda1fa353b3368ab356` |

The complete executable hash is calculated from the running process's module
path before attachment. Unknown hashes are reported but never opened for ERS
writes. Hash results are cached only while file length and last-write time stay
unchanged.

The multi-game controller requires a single supported active game. If both
supported games are running, it exposes an unsupported snapshot, rejects every
ERS operation, and refuses global game-window hotkeys until only one remains.

## Process access

After the full hash gate passes, the game process is opened with VM read, VM
write, VM operation, limited query, and synchronize rights. The process ID,
module base, executable hash, and liveness remain part of the active connection
state. F1M24 also records process start time for save freshness checks.

Each refresh and action fails closed if the process has exited, the handle is
invalid, the verified hash no longer matches, the required pointer chain cannot
be resolved, a memory read is incomplete, or runtime values leave their allowed
ranges.

## F1 Manager 2023 car resolution

The F1M23 controller derives every runtime address from the verified module
base. It resolves the engine, viewport, world, game state, and race manager
chain, then examines the race-manager car cache.

A candidate must match the verified car actor layout and pass all of these
checks:

- team-car ID is exactly 1 or 2;
- that team-car ID resolves unambiguously;
- the live car actor and vehicle relationship is consistent;
- driver number and staff ID are readable and stable;
- fuel is finite and within the expected race range;
- ERS is finite and between 0 and 100%;
- the separate presentation value is also between 0 and 100.

Both player-car slots must be available before the controller publishes a
supported snapshot.

## F1 Manager 2024 car resolution

The F1M24 controller resolves the verified car-array root twice and requires
the same result. It scans the fixed 22-car race array and validates vehicle,
driver, team, driver-number, staff-ID, fuel, and battery fields.

F1M24 does not infer the player team from array order. It reads the current
save from:

`%LOCALAPPDATA%\\F1Manager24\\Saved\\SaveGames`

The save reader:

1. opens the file read-only;
2. checks size limits and stable file length/write time;
3. validates and inflates the embedded database in memory;
4. parses the required SQLite records without starting a database process;
5. extracts one player TeamID and exactly two current driver staff IDs.

The selected save must not predate the running game process. The save's TeamID
must match exactly two adjacent live race cars, and its two driver IDs must
match those cars without ambiguity. A changed save stamp invalidates the cache,
stops active F1M24 holds, and requires complete revalidation.

The save file and embedded database are never modified.

## ERS writes

The battery is stored as a single-precision fraction. The controller accepts a
percentage from 0 through 100, converts it to a fraction from 0 through 1, and
writes exactly four bytes to the already validated battery address.

Immediately before every write it repeats the exact-build and car-identity
checks. Immediately afterwards it reads the field back and accepts success only
when the value is still valid and matches the requested fraction within a small
floating-point tolerance.

For a two-car one-time action, both original values are read first. If a later
write fails, the controller attempts to restore every earlier car in that
operation and reports whether the rollback was complete.

No code pages, pointers, game functions, executable imports, DLLs, or save
files are changed.

## Hold behavior

Holding ERS stores the validated current fraction separately for each selected
car. A timer runs every 75 ms while at least one hold is active. Every tick
revalidates the exact process and full car identity before reapplying the
four-byte value.

If a target becomes invalid, only that target's hold is removed. If the build,
process, save identity, or overall connection becomes invalid, all holds stop.
Closing the application stops the timer, clears every stored hold, closes the
process handle, and disposes both game controllers.

## Overlay

The overlay consumes immutable UI snapshots. It never calls process-memory or
save-reading functions.

It is a separate 300×48 topmost Windows Forms window with no activation,
taskbar entry, or input handling. `WS_EX_TRANSPARENT`,
`WS_EX_TOOLWINDOW`, `WS_EX_NOACTIVATE`,
`HTTRANSPARENT`, and `MA_NOACTIVATE` keep mouse and
keyboard focus in the game.

The badge is shown only when:

- the user enabled it;
- the snapshot is supported and contains both valid car percentages;
- the foreground-window process ID matches the verified game process;
- the game window is visible and not minimized;
- the verification timestamp is no more than 0.8 seconds old.

The window follows the verified game's client area and monitor DPI. Left HUD
gap uses three quarters of the available client height to sit between the
standings and left driver panel. Overlay failures hide the badge without
changing ERS or the selected target.

## Hotkeys and target selection

Five unique shortcuts are registered with `RegisterHotKey` and
`MOD_NOREPEAT`. Registration is atomic at startup: if Windows has
already reserved one binding, all registrations from that attempt are released
and the UI reports the conflict.

ERS actions are accepted only while the manager or exact verified game is
foreground. Overlay toggle and target cycle are always allowed while the
manager itself is foreground because they do not write game memory. In-game use
still requires the exact supported foreground process.

Target cycling changes only the UI selection:

`Car 1 → Car 2 → Both cars → Car 1`

The F8, F9, or F10 action performed afterwards reads that selection. Dispatch
handles only those three ERS actions; display-only actions cannot fall through
to a battery write.

Hotkey settings are saved atomically through a temporary file. Older settings
without overlay or target-cycle actions receive unused migration shortcuts
without replacing an existing custom binding.

## Tests

`source\\build.cmd` produces a temporary console build and runs 67
isolated checks before completing the Windows executable. The suite covers
settings migration, target cycling, overlay labels and placement, snapshot
freshness, foreground matching, supported hashes, telemetry ranges, F1M24 save
parsing, multi-game fail-closed behavior, and the safe fallback controller.

Self-tests do not open or write a game process.

## Limitations

Runtime layouts are build-specific. A game update requires a new executable
hash and a fresh review of every address and identity check. The overlay is an
external desktop window and may be hidden by true exclusive fullscreen.

The utility is unsigned. Process-memory access can resemble trainer behavior
to security software even though the controller restricts writes to the
validated battery field.
