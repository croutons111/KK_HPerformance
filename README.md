# KK_HPerformance

**English** | [日本語](README.ja.md)

> A BepInEx plugin for Koikatsu that **shortens the freeze when an H position starts or changes** and **raises the FPS when many characters are on screen**.

---

## Overview

Koikatsu re-reads a lot of data from disk every time an H position starts or changes, so the whole screen stops for a moment — even in the unmodded game. With many characters on screen, hair / skirt / breast physics and the detailed semen effect can also pull the FPS down hard.

KK_HPerformance works on both:

- **Shorter freezes** — animations and H data tables are kept in memory during the H scene, so a position you have already used starts much faster the next time.
- **Higher FPS**
  - Hair / skirt / breast physics no longer piles up extra work when the FPS drops.
  - Physics is paused for characters that are off screen.
  - The detailed semen effect is rebuilt less often and settles sooner, and the hitch right after ejaculation is much smaller.
- **Diagnostics (optional)** — profilers that log what is slow, for troubleshooting.

It hooks into the game's own loading and physics, so it helps the game's characters and characters added by other plugins alike. Nothing changes in how H scenes look or play.

---

## Requirements

| Item | Requirement |
|---|---|
| Game | Koikatsu (HF Patch) |
| Framework | BepInEx 5.4.x |
| Dependencies | None |
| VR | Not tested |

---

## Installation

1. Download the latest `KK_HPerformance.dll` from [Releases](../../releases).
2. Place it in your `BepInEx/plugins/` folder.
3. Start the game.

---

## How to Use

Nothing to do — it works as soon as it is installed.

- **Freeze reduction** applies in H scenes. The first time each position is used it still loads normally; from the second time on it is faster.
- **Physics and FPS improvements** apply in every scene (including Studio). The physics limit only kicks in when the FPS falls below 60.
- **Semen effect improvements** apply when the ejaculation effect is set to **Detailed** in the game settings.

To go back to the original behavior, turn off `General > Enabled` in the config. It takes effect immediately.

---

## Configuration

Settings can be changed in-game with the ConfigurationManager (F1).

### General

| Setting | Default | Description |
|---|---|---|
| Enabled | ON | Turns the whole plugin on or off. OFF = original game behavior |

### Cache (freeze reduction)

| Setting | Default | Description |
|---|---|---|
| Animator Controllers | ON | Keeps position animations in memory instead of re-reading them every time |
| Controller Cache Lifetime | HScene | `HScene` = clear when the H scene ends. `Session` = keep until the game closes (faster from the first use in later H scenes, uses more memory) |
| List Texts | ON | Keeps the H data tables (reactions, sounds, attachments etc.) in memory |
| Bundle Lookups | ON | Remembers which data files exist and what they contain |

### Performance (FPS)

| Setting | Default | Description |
|---|---|---|
| Dynamic Bone Max Steps | 1 | How many times per frame hair / skirt / breast physics may run to catch up when the FPS drops (1–3). 3 = original game |
| Dynamic Bone Off-Screen Culling | ON | Pauses physics for characters that are off screen or hidden |
| Culling Screen Margin (m) | 0.5 | Extra space around each body when checking whether it is on screen (so long hair or skirts still count) |
| Culling Max Distance (m) | 0 | Also pauses characters farther than this from the camera. 0 = off |
| Metaball Rebuild Interval | 2 | Rebuilds the detailed semen effect every N frames (1–4). 1 = original game |
| Metaball Settle Time | 6 | Seconds until a semen drop settles in place (2–10). 10 = original game |
| Metaball Cell Budget | 0 | Makes the semen mesh coarser only when a rebuild would be very large. 0 = no limit |

### Debug

| Setting | Default | Description |
|---|---|---|
| Load Profiler | OFF | Logs how long loading takes and breaks down frames that freeze |
| Load Profiler Hitch Threshold (ms) | 100 | Frames longer than this are reported with a breakdown |
| Load Profiler Min Log (ms) | 5 | Loads shorter than this are not logged individually |
| Frame Profiler | OFF | Logs which parts of each frame are heaviest (game and plugins), with the FPS and number of characters |
| Frame Profiler Report Interval (sec) | 5 | How often the summary is logged |
| Frame Profiler Top N | 20 | How many of the heaviest items are listed |

To get the original game behavior while keeping the plugin on, set `Dynamic Bone Max Steps` = 3, `Dynamic Bone Off-Screen Culling` = OFF, `Metaball Rebuild Interval` = 1 and `Metaball Settle Time` = 10.

---

## Notes

- The profilers in **Debug** are for troubleshooting only. They do not change anything in the game, but measuring itself costs a little FPS. Turn them off when you are done.
- When a paused character comes back into view, its hair / skirt is reset to the current pose before physics resumes, so nothing stretches or snaps.
- `Controller Cache Lifetime = Session` keeps animations loaded for the whole play session, which uses more memory.

---

## License

Distributed under the **GNU General Public License v3.0**. See [LICENSE](LICENSE).

- Modification and redistribution must follow the terms of GPL v3.0.
