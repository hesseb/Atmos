# CLAUDE.md

## Project context

This repo is the implementation component of a Master's thesis on physically
based rendering of atmospheres and clouds for strategy games. The pre-study
report lives at C:\dev\thesis\thesis-report (not this repo).

**Research question:** how does a physically based atmosphere/cloud renderer
compare — visually and in performance — to a traditional simplistic
skybox/textured approach, in a strategy-game context (top-down/tilted
camera, planet- or region-scale view, not a first-person flight sim).

**Base project:** Sebastian Lague's "Geography" game/tech demo (globe world,
flyable camera/plane). We are stripping it down to a minimal scaffold
(globe + camera + minor strategy-like UI) and building two renderers on top:
1. A **baseline** renderer: simple textured skybox / cheap atmosphere approximation.
2. A **PBR** renderer: physically based multi-scattering atmosphere + volumetric clouds.

Both must be swappable/comparable under the same camera paths and scene, with
performance and image output logged identically, for thesis figures/tables.

## What matters vs. what doesn't

**Keep:**
- Globe/planet representation and LOD terrain
- Camera and controls
- Core render pipeline hookup points

**Removed** (done — commit `e7764ae`, see NOTES.md):
- Menus, HUD, localization, audio, the input-rebinding layer
- The player aeroplane, packages, parachutes, quests, hot air balloons
- The in-game delivery map globe (a second globe on layer 7 with its own camera)
- `GameController`/`GameState` — a testbed has no game states, and
  `SolarSystemManager.SetTimes` is a better time API for measurement anyway

**Kept despite looking removable** — don't delete these without reading NOTES.md:
- `Assets/Scripts/Editor Helper/` — deliberately **not** a real `Editor` folder despite
  the name: `AtmosphereEffect.cs` and `StarRenderer.cs` are ordinary runtime scripts in
  `Assembly-CSharp` and reference `EditorShaderHelper` under `#if UNITY_EDITOR`, but a
  folder literally named `Editor` compiles into `Assembly-CSharp-Editor` instead — a
  separate assembly that `Assembly-CSharp` cannot see into, guard or no guard. Renaming
  this folder to `Editor/` breaks the build; verified the hard way once already
- `WorldLookup` — idles, but is the only terrain-height-at-a-coordinate query, which
  the measurement harness needs
- `PlaceholderWorld` — deactivates itself on play; the only thing showing where the
  planet is in the Scene view before terrain loads
- `Assets/Scripts/Generation/` — offline bake tools for the 900 MB in `Assets/Data`

**Not in scope for this thesis:** any systems tied to actual gameplay. This project isn't an actual game, just a tech demo. So score, credits, menu, and so on is not relevant.

## Target feature list (rough milestone order)

1. ~~Strip Geography down to globe + camera scaffold~~ **done** — `TestbedCamera`
   (orbit + free-fly), scene down to 21 GameObjects
2. ~~Controls and UI: renderer swapping, globe navigation, time controls~~ **done** —
   `TestbedCamera`, `TimeController`/`SolarTime`, and the F1–F10 overlay in
   `BenchmarkHud`; renderer arms switch through `PostProcessRendererProfile`
3. ~~Measurement harness: fixed camera paths, frame timing / GPU timers, screenshot
   capture, exportable data~~ **done** — `Assets/Scripts/Testbed/Benchmark/`, CSV +
   run.json, 13 `ProfilerRecorder` counters, `plan_hash` and a bake-staleness stamp
4. ~~Baseline skybox/simple atmosphere renderer~~ **done** — `BaselineSkyRenderer`,
   `AerialPerspectiveSimple`, plus a null arm for each so the pass structure can be
   priced separately from what it draws
5. ~~PBR atmosphere~~ **done** — transmittance, multiple-scattering, sky-view and
   aerial-perspective LUTs, aligned with Hillaire 2020; see NOTES.md
6. ~~Volumetric cloud rendering~~ **done** — two bands, weather/shadow/sky maps,
   Full/Half/Temporal cost modes, plus a baseline cloud arm in two deliveries
7. Comparison tooling / parameter sweeps for thesis evaluation — *profiles and the
   subtraction algebra exist; two authoritative measurement passes are recorded in
   NOTES.md (dev vs release at 1440p, and a SelfCheck baseline pass), but the full
   sweep matrix for the thesis report is not run yet*

Effectively everything but the measurements and the report is assembled. What remains
is the optimization phase NOTES.md scopes, the runs, and the writing.

## Working conventions

- Implementation of the renderer should largely follow the methods described by the pre-study and its sources
- Prefer small, reviewable changes over large refactors in one go.
- After stripping/removing a system, confirm the project still builds and runs
  before moving to the next one.
- When implementing the PBR atmosphere, cite/follow the pre-study described techniques rather than
  inventing an ad hoc approximation, since thesis methodology needs to be
  traceable to a real technique.
- Keep the baseline and PBR renderers switchable at runtime or via a simple
  toggle/config, not as permanently divergent branches — needed for
  apples-to-apples comparison.
- Summarize decisions/status at the end of a work session so it can be
  carried into the next one (e.g. append to NOTES.md).
