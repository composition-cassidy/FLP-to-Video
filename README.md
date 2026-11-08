# FLP to Video

A VEGAS Pro script that places your clip on the timeline following an FL Studio project (`.flp`).
Made for YTPMV / Sparta Remix editing. No MIDI export needed.

## What it does

- Reads notes and channel-rack steps straight from the `.flp`
- Gives step-sequencer hits a real length (no more clips held until the song ends)
- Handles slide notes: continue the clip, pop to a pose, or toggle a pose on/off
- Cuts chopped playlist audio clips from the same spot in your video
- Follows tempo automation, skips muted tracks/clips, supports multiple arrangements
- Optional pitch from the notes (root C5)

## Requirements

- Windows
- VEGAS Pro 16 or newer
- FL Studio projects (tested with FL Studio 2026)
- No Python needed. `flp2json.exe` is included.

## Install

1. Download **FLP to Video v1.0.zip** from the [Releases](../../releases) page and unzip it.
   (Or use **Code > Download ZIP** on this page and rename the unzipped folder to `FLP TO VIDEO`.)
2. Copy the **FLP TO VIDEO** folder into:
   ```
   Documents\Vegas Script Menu\
   ```
   Keep `flp2vid.cs` and `flp2json.exe` together in that folder.
3. Restart VEGAS.
4. The script is now under **Tools > Scripting > FLP TO VIDEO > flp2vid**.

> Windows may warn about `flp2json.exe` the first time because it isn't signed. Allow it to run.

## How to use

1. Click the clip you want to place on the VEGAS timeline.
   For chopped audio clips, select the same event you rendered the sample from.
2. Run **flp2vid** and pick your `.flp`. Next time it reopens the same project automatically.
3. Tick the channels you want, adjust the tabs (Placement, Timing, Notes, Effects), and press **Generate**.
4. Don't like it? One **Ctrl+Z** undoes the whole run.

**Batch tip:** name Project Media subclips after your FL channels or sample files.
They're matched automatically, so each channel gets its own clip in one run.

## Files

| File | What it is |
|---|---|
| `flp2vid.cs` | The VEGAS script |
| `flp2json.exe` | Reads the `.flp` file (built with PyFLP) |
| `source\flp2json.py` | Source code for `flp2json.exe` |

Settings and presets are saved in `%APPDATA%\FLP to Video\`.
