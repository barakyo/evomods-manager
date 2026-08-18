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

## Saira Condensed, Rajdhani — SIL Open Font License 1.1

<https://fonts.google.com/specimen/Saira+Condensed> · <https://fonts.google.com/specimen/Rajdhani>

Saira Condensed by Omnibus-Type; Rajdhani by Indian Type Foundry. Both are shipped inside the app,
in `EvoMods.App/Assets/Fonts`, because neither is a Windows font and the alternative is a silent
fallback that looks almost right. Saira Condensed sets headings, Rajdhani sets the camera readouts —
the same jobs they do on evomods.gg.

The OFL permits bundling in this way. Its one hard condition is that the fonts are not sold on their
own, which is not a thing that could happen here. Running text stays on Segoe UI Variable rather than
the site's Inter: it is close enough that shipping a second neutral grotesque would buy nothing.

---

## Pure — Peter Boese

<https://www.patreon.com/peterboese>

Not bundled, not required, and no code from it is used. It is credited because the tone curve in
four of the five post-processing filters this app installs — `Video_Hero`, `Video_Hero_Soft`,
`Video_Punch` and `Video_Cine` — was produced by a least-squares fit of EVO's five-parameter curve to
Pure 2.57's response. The numbers are ours; the look they reproduce is Peter Boese's paid work, and
those four filters are derivative of it.

`Video_Clean` is not. It uses Kunos' own Natural curve with grading on top.

---

# What this tool redistributes, and what it does not

The Flat Pad track is **derived on your machine from your own copy of the game**. Its geometry,
textures and irradiance volumes originate from Assetto Corsa EVO (© Kunos Simulazioni) and are never
shipped with this tool.

The post-processing filters are the exception, and the one place this repository does carry content
that started as a game asset. Each of the five `.postprocessing` files under
`EvoMods.Core/Filters/Assets`, and the `exposure_compensation.curve` beside them, began as Kunos'
`natural1` equivalents with between three and nine values changed — `video_clean` differs in exactly
three. They are 5,577 bytes in total, they contain no geometry, textures or imagery, and they are
parameter sets rather than assets in any meaningful sense. But "nothing in this repository contains
game assets", which this file used to say, is no longer true, and saying so plainly is better than
keeping a claim that has quietly stopped holding.

Every curve those filters actually reference — six of the seven each — is left to resolve against the
player's own install. Nothing from `natural1` is copied.
