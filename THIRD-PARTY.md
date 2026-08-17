# Third-party components

## ACEvo.Package — Nenkai

<https://github.com/Nenkai/ACEvo.Package> · MIT License · Copyright (c) 2025 Nenkai

Reads and extracts Assetto Corsa EVO `.kspkg` archives. This installer references the
`ACEvo.Package` **library** (not its CLI) to detect and unpack the game archive, because a track can
only be loaded from an unpacked install.

The full licence text ships alongside the library in `external/ACEvo.Package/LICENSE.txt`.

Referenced as a git submodule and used through its **public API only** — no fork. Its `ExtractAll`
has no progress or cancellation hook and its file table is private, so extraction is driven one
entry at a time from our side instead. Keeping the submodule unmodified means
`git submodule update` stays a safe way to pick up format fixes, and the pack format *has* changed
across game versions.

### Worked around here, worth fixing upstream

Both live in `PackFile.Open` / `DetectFileTableSize`. Neither is patched in the submodule; if a
future version fixes them, the workarounds in `EvoMods.Core/Game/PackageUnpacker.cs` become dead
weight and can go.

- **No bounds check before seeking to the file table.** `fs.Position = fs.Length - tableSize` is set
  without checking the file is that long, and the 64 MB candidate is tried first. Any `.kspkg`
  smaller than 64 MB throws `ArgumentOutOfRangeException` on the first iteration and never reaches
  the 32 MB candidate — so a small package built for a pre-0.7 game cannot be opened at all. Current
  packers write a fixed 64 MB table regardless of payload, which is why real mod packages avoid it.
  Fix: `if (tableSize > fs.Length) continue;`.
- **A failed `Open` leaks the file handle.** `File.OpenRead` happens before any validation, and the
  stream stays a local until the `PackFile` that would own it is constructed at the very end. Every
  failure path therefore leaves the package open, with nothing public holding a reference to close
  it — so picking the wrong file leaves it locked against being moved or deleted. Fix: `try`/`catch`
  around the body with `fs.Dispose()`, or construct the `PackFile` first.

---

# What this tool does *not* redistribute

The Flat Pad track is **derived on your machine from your own copy of the game**. Its geometry,
textures and irradiance volumes originate from Assetto Corsa EVO (© Kunos Simulazioni) and are
never shipped with this tool. Nothing in this repository contains game assets.
