# Flat Pad Installer

A one-click installer for unpacking Assetto Corsa EVO and installing the **Flat Pad map**.
Flat Pad is a 1.5 km dead-flat, wall-free, scenery-free test pad for
_Assetto Corsa EVO_ that spawns your car ready to drive. Built for physics testing, where driving
800 m across a real circuit before every run gets old fast.

## How it works

There are essentially 3 steps to installing a new map:

1. Unpack the game
2. Add the map
3. Update the track table

### Step 1: Unpacking

Assetto Corsa EVO ships with a `content.kspkg`. Essentially, when the game loads up, it looks for
this file and reads all of the game's content from the file. If the file is not there, it looks for
content in the game's `content/`. Step 1 of installing a new track, first requires unpacking the game.

To unpack the game, this script builds on top of [Nenkai's ACEvo.Package](https://github.com/Nenkai/ACEvo.Package).
This portion mainly provides a GUI to make running the command a little more user friendly.

Unpacking roughly **doubles the install**: the archive is kept (renamed `content.kspkg.bak`) and its
~70 GB of contents are written out alongside. The tool computes that figure from your archive rather
than hardcoding it, shows it before touching anything, and makes unpacking an explicit confirmed
step. **Nothing is renamed until every file is out**, so cancelling or crashing halfway leaves the
game exactly as playable as it was.

**Note:** While unpacked, do **not** use Steam's _Verify integrity of game files_ — it re-downloads the whole
archive. A game update also restores packed mode; re-running the tool fixes that.

### Step 2: Add the map

The Flat Pad map was created by taking a clone of sebring and stripping away everything until the only
thing left was just the track surface. The second part of this script does exactly that.

### Step 3: Update the track table

Unfortunately adding the map to the `content/` folder is not enough. A track only appears in the menus
once it is registered in two `system\*.table` registries.

Base-game content is left byte-identical to what Kunos ships. Registering reads the **live**
registry and strips out this tool's own entries before adding them back, so re-running can never
stack duplicates. It deliberately does not restore a snapshot taken on a previous run: a snapshot
from before a game update would silently revert whatever that update added, which once cost a
base-game track its entry outright.

## Unpacking a single package

Not one of the three steps above, and nothing to do with installing a map. **Unpack a .kspkg…**
points at any package — a car mod from `Saved Games\ACE\mods\`, or the game's own archive — and
writes its contents into a folder you choose. You can drag a `.kspkg` onto the window instead. It is
the one action that needs no game folder at all: taking a mod apart to see how it is built should not
require owning the game on that machine.

Nothing about the install changes. No archive is renamed, no registry is touched, and the package is
left exactly where it was. Before writing anything it shows the file count, the total size, the folder
every file shares — `content\cars\nissan_skyline_r34_gtr\`, so you can see which car you have — and
the free space where it is about to land. Point it at your own `content.kspkg` and it says so,
because copying the archive is _not_ the same as unpacking the game and the two are easy to confuse.

Cancelling is checked between files, never during one, so every file on disk when you stop is
complete rather than half-written.

## Building

Needs the [.NET 10 SDK](https://dotnet.microsoft.com/download), and the submodule:

```
git submodule update --init
dotnet build FlatPadInstaller.slnx
dotnet test  FlatPadInstaller.slnx
```

A portable single-file build — one ~49 MB `.exe`, no runtime to install first:

```
dotnet publish FlatPad.App -c Release -o out          # -> out/FlatPadInstaller.exe
```

That is the whole build. The three `.pdb` files beside it are debug symbols and are not needed to
run — the exe carries the .NET runtime and its native libraries inside itself, which is the 49 MB.

To cut a release, add the version to the filename **from a terminal**, and upload that:

```
mv out/FlatPadInstaller.exe out/FlatPadInstaller-v1.0.0.exe
sha256sum out/FlatPadInstaller-v1.0.0.exe
```

⚠️ Not from Explorer. It hides known extensions, so typing a name ending in `.exe` over a file whose
`.exe` is already hidden produces `…exe.exe` — which on an unsigned download reads as the oldest
malware disguise there is. This has happened once already.

Two builds of the same commit are not byte-identical (.NET stamps a fresh build id), so publish the
hash of the file you actually upload. The exe reports the commit it was built from in its file
properties, which is the durable way to identify a build.

## Using the dev CLI

The GUI is the product; this exists so the logic can be driven, and diffed against the reference
implementation, without one.

```
dotnet run --project FlatPad.Cli -- status
dotnet run --project FlatPad.Cli -- unpack
dotnet run --project FlatPad.Cli -- install
dotnet run --project FlatPad.Cli -- verify
dotnet run --project FlatPad.Cli -- repair
dotnet run --project FlatPad.Cli -- uninstall
dotnet run --project FlatPad.Cli -- revert
dotnet run --project FlatPad.Cli -- check-unpack

# standalone packages — these need no game folder at all
dotnet run --project FlatPad.Cli -- inspect-package --input "<file.kspkg>"
dotnet run --project FlatPad.Cli -- unpack-package  --input "<file.kspkg>" --out "<dir>"
```

`--game <path>` is auto-detected from Steam if omitted. Every command is idempotent, and Ctrl+C
cancels cleanly. `verify` is read-only, and reports a **count** for every check — a validator that
finds nothing to check would otherwise print a cheerful `PASS`. `check-unpack` samples the loose
files against the archive they came from, which is how you tell a finished unpack from one that
quietly ran out of disk.

### Show me exactly what you changed

Registering a track means writing into `system\tracks.table` and `system\track_containers.table` —
files the game owns and every track mod shares. Get that wrong and there is no error and no log
line; the affected track is simply absent from the menus. This tool got it wrong twice, and neither
time was visible.

So `verify` also reads the two registries **as the game ships them**, straight out of your
`content.kspkg`, and compares:

```
  registry vs stock (content.kspkg.bak): 20 catalog + 179 session entries in stock, 0 missing live
```

Anything present in stock, missing live, and whose track files are still on disk is a **failure**,
whatever caused it. `repair` puts those entries back — cloned verbatim from the archive, so each
keeps the id and menu index the game gave it, and nothing else in the registry is touched. In the
GUI this is offered after a `Verify` that found damage, never as a standing button.

Two things it will not do. It declines when several renamed-aside archives are present, because
which one matches your build is a guess (`revert` refuses for the same reason). And an entry whose
track folder is _not_ on disk is reported as "not installed here" rather than damage — that is what
an archive from a different game version looks like, and there is nothing to fix.

`--pr-runoff-spawn` from the Python is deliberately **not** ported: it moves a spawn on a _base-game_
track, the one thing this tool exists to avoid. `uninstall` still reverts it if an older run left it
behind.

## Verification

The Python script stays authoritative until the port is confirmed in-game. The two agree
byte-for-byte, on the console output _and_ on the files produced:

```
uv run python ../tracks/install_flatpad.py --verify           > py.txt
dotnet run --project FlatPad.Cli -- verify --game "<game>"    > cs.txt
diff py.txt cs.txt

# stronger: install with each, and hash everything they wrote
( cd "<game>" && find content/tracks/flatpad -type f | sort | xargs sha256sum ) > tree.sha256
```

|                      |                                                                                                                                                                                                                                               |
| -------------------- | --------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| Track build          | **Byte-identical** to the Python across all 1530 files, from a warm install and a cold start. Uninstall matches too.                                                                                                                          |
| Verify               | Console output byte-identical, on a passing install _and_ on a deliberately broken one.                                                                                                                                                       |
| Archive reading      | 201 files sampled from the real 67.5 GB archive extract byte-identical to disk, across its 115,896-file table. |
| Unpack round trip    | Run end-to-end against a real 599 MB `.kspkg`: detect → free-space check → extract all 650 files → rename aside → detect unpacked → revert → detect packed. Output is **byte-identical to Nenkai's own CLI**.                                 |
| Package unpack       | `unpack-package` on the same 599 MB car mod: 607 files / 532.1 MB out of 650 entries (43 are folders), SHA-256 **identical to Nenkai's own CLI** on every file and no extras. `inspect-package` reports those counts and the shared root without extracting. |
| Package cancellation | Cancelled mid-unpack at three points — 30, 570 and 607 of 607 files. Every file on disk was byte-identical to a full extract; **none truncated**. A hard `Stop-Process` at the same point _does_ leave one 0-byte file, which is what the between-files check exists to avoid. |
| Unpack at full scale | Run once for real through the GUI: **119,443 files / 68.5 GB**, then reinstall and verify. The whole round trip left all 1530 installed files **byte-identical** to the pre-run baseline.                                                     |
| Registry repair      | Rehearsed against a real registry: a scratch copy of the live tables with a real base track's 5 entries deleted, repaired from the real 72.5 GB archive. All 5 came back **byte-identical** to the pristine file, and no other entry changed. |
| Unit tests           | 134, covering the format layer, the closure crawl, the geometry edits, the archive state machine, progress throttling, registry integrity, the file-table parse and where an entry is allowed to be written. |

**Console divergence from the Python.** The reference implementation is behind: the five bugs a
real-world v0.8.1 run exposed were fixed here and never back-ported, and it cannot read a `.kspkg`
at all, so it has no registry-integrity check. Today `diff py.txt cs.txt` on a healthy install shows
four hunks, and the verdicts differ — the Python still reports the _base game's_ own dangling
reference as a failure. Re-measure it rather than trusting a remembered number. The file-level
comparison is the check that matters, and it is unaffected: all 1530 bytes still agree.

## Layout

|                         |                                                                                                                                       |
| ----------------------- | ------------------------------------------------------------------------------------------------------------------------------------- |
| `FlatPad.Core/Protobuf` | Lossless raw-protobuf tree. Re-emits a node's original bytes unless it was modified, so an untouched file round-trips byte-identical. |
| `FlatPad.Core/Refs`     | Reference extraction, the `content\…` closure crawl, and copy-with-repath.                                                            |
| `FlatPad.Core/Scene`    | Reading and reshaping the geometry a track scene is made of.                                                                          |
| `FlatPad.Core/Tables`   | The `system\*.table` registry editor.                                                                                                 |
| `FlatPad.Core/Game`     | Finding the install, switching it between packed and unpacked, reading stock files back out of the archive, and unpacking a standalone `.kspkg`. |
| `FlatPad.Core/FlatPad`  | The Flat Pad recipe itself: build, install, uninstall, verify, repair.                                                                |
| `FlatPad.App`           | The WinForms GUI — a thin shell, no logic of its own.                                                                                 |
| `FlatPad.Cli`           | Dev entry point. Not shipped.                                                                                                         |

## Licence

MIT — see [`LICENSE`](LICENSE). Third-party components and the asset-redistribution position are in
[`THIRD-PARTY.md`](THIRD-PARTY.md).
