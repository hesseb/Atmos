# NOTES.md

Running log of decisions and status, so a work session can be picked up cold.
See [THESIS.md](THESIS.md) for the research question and theory, [START.md](START.md)
for the audit of the inherited codebase.

---

## 2026-08-09 — Milestone 1: strip to a rendering testbed

Branch `testbed-strip`, off `main` at `31efe80`. Full plan:
`C:\Users\Alex\.claude\plans\delegated-snacking-moth.md`.

### Decisions taken
- **Camera**: new `TestbedCamera` with two modes — globe orbit (the mode the thesis
  measures in) and free-fly debug. Replaces `GameCamera`, which followed the aeroplane.
- **Map globe** (the in-game delivery minimap under `UI/HUD/Map`): deleted entirely,
  along with the PathCreator plugin that only it used.
- **Input**: legacy `Input.GetKey`. All of `Assets/Scripts/Input/` goes, including the
  generated `Player Action.cs` and its `.inputactions` asset. `activeInputHandler: 2`
  (Both) stays — removing the Input System package while it is set to 2 produces a
  project that will not build.
- **`Assets/Scripts/Generation/`**: kept as-is. Fully decoupled, and the only way to
  regenerate the 924 MB of baked terrain in `Assets/Data`.
- **`GameController` / `GameState`**: to be deleted outright, not stripped. Only one
  keep-side call site survives, and `SolarSystemManager.animate` + `SetTimes()` are a
  strictly better time-control API for a measurement harness — exact and reproducible,
  where a `GameState` stack mutating `Time.timeScale` is neither.
- **No camera smoothing.** Every pose is a pure function of the state fields, so a given
  view is reproducible between runs. Reproducibility beats feel here.

### Status: **milestone 1 complete.** Verified running by AH.

| Commit | Stage | What |
|---|---|---|
| `149cafd` | 0 | THESIS.md, START.md, CLAUDE.md |
| `8e4cdde` | 1–2 | Decoupled `RenderSettingsController` and `LoadingManager` from `Menu/` |
| `87029c9` | 3–4 | `TestbedCamera`; boot to `Playing`; raised camera cull distance |
| `9a5ce2f`, `1b164e7` | 4 | Home view + reset key (Backspace) |
| `bd8ca9d` | 5, 8 | Decoupled `LoadingManager` from globe map, `SolarSystemManager` from `GameController` |
| `e7764ae` | 5–8 | Deleted the gameplay cluster — 443 files, 77,660 lines |

**Result:** scene is 21 GameObjects across 3 roots (`Game`, `Loading Manager`,
`Editor Only`), down from 337 across 7. 335 C# files, down from 403. Exactly one
camera, one audio listener, one light.

Two hidden compile edges pointed from keep-code into `Menu/` and had to be cut before
anything could be deleted: `struct Settings` is declared inside `Menu/SettingsMenu.cs`
but consumed by `RenderSettingsController`, and `LoadScreen` was consumed by
`LoadingManager`. Both resolved by deleting the consuming code, which turned out to be
dead — no relocation needed.

`LoadingManager.SetActiveStateAll` is now null-tolerant. **Keep that guard.**
`deactivateWhileLoading` holds `HUD`, which lives inside the `UI` root slated for
deletion, and this runs at execution order `-1100` where an NRE aborts the entire world
bootstrap behind one misleading error.

### Camera controls
Orbit mode: `WASD`/arrows pan (great-circle, so speed is uniform at every latitude),
`Q`/`E` heading, `R`/`F` pitch, scroll to zoom. Free-fly: `WASD` + hold RMB to look,
`Shift` fast, `Alt` slow, `Space`/`Ctrl` up-down. `Tab` toggles mode, `Backspace` resets
to the home view. Home is currently 36.76°N 1.12°W (southern Spain), altitude 40,
pitch 70° — captured via the component's *Set Home To Current View* context menu.
`startAtHomeView` (default on) makes play mode open on that same view, so the demo
always starts from a known shot regardless of where the scene was last saved.

A view is `(lon, lat, altitude, heading, pitch, roll)` — a complete pose, so anything
captured in either mode restores exactly. Pitch runs −90 (zenith) through 0 (horizon)
to +90 (nadir); `F` tilts up past the horizon so the sky can be framed without
dropping into free-fly.

**Bookmarks:** `Z` `X` `C` `V` jump to saved views; `Shift`+key overwrites that slot with
the current view. They live in a `CameraBookmarks` ScriptableObject asset, *not* on the
component — Unity discards play-mode changes to scene components but keeps them for
assets, so a view captured while flying survives exiting play mode. Press `Ctrl+S` after
capturing to flush the asset to disk. Empty slots log a hint rather than silently doing
nothing. The same asset is the natural home for the harness's fixed camera paths.

### Time controls (`TimeController` + `SolarTime`)
Speed: `,` / `.` step through 0.1×…256×, `P` pauses. Presets, all relative to wherever
the camera currently is: `1` sunrise, `2` noon, `3` sunset, `4` midnight, `5` golden hour
(configurable elevation, default 5°). `F1` toggles the overlay. Same presets are on the
component's context menu, so shots can be composed without entering play mode.

The presets are **solved analytically, not searched**. In geocentric mode the sun
direction is `Ry(360·(dayT+yearT)) · Rz(tilt) · (−earthPosNormalised)`, so sun elevation
at a fixed observer is a pure sinusoid in `dayT` and inverts in closed form. Verified
against brute-force evaluation of the real orbit code to 3×10⁻¹⁵, with every target
elevation landing within 0.001° and the correct rising/setting branch. This means
"sunset" is exactly 0° and descending, reproducibly, which is what the RQ1 comparison
needs — twilight is where ozone and the Mie forward lobe do most of their visible work.

`SolarTime` is pure maths with no scene state, so the harness can call it directly.
`TrySolveDayT` returns false rather than guessing at polar day/night or at a pole, where
elevation doesn't vary over the day at all.

**Two things that must be off when measuring:** `TimeController.showOverlay` (IMGUI
allocates and costs frame time) and `SolarSystemManager.animate` (pin the sun with
`SetTimes` instead, or the sun moves between the two renderers' captures).

---

## 2026-08-09 — Benchmark harness (milestone 3, in progress)

Branch `benchmark-harness`. Plan: `C:\Users\Alex\.claude\plans\delegated-snacking-moth.md`.

### Measured facts from `FrameProbe` — do not re-derive these

Editor run, 600 measured frames, `Time.captureDeltaTime = 1/60`, after enabling
**Frame Timing Stats** and **Run In Background** in Player Settings.

| Question | Answer |
|---|---|
| Does `captureDeltaTime` throttle? | **No.** Mean wall 8.42 ms vs 16.67 if it slept. `cpuFrameTime` 8.4169 ≈ wall 8.4192 — the frame-locked design is sound. |
| Which clock does it pin? | **`Time.deltaTime` only.** `Time.unscaledDeltaTime` ranged 2.25–18.34 ms, i.e. still real time. Assert the frame lock on `deltaTime`. |
| GPU timing available? | **Yes**, mean `gpuFrameTime` 1.88 ms. |
| Timing lag | **1 frame** (assumed 3–5). |
| Per-frame attribution | **Exact** — 600 timings over 600 frames, exactly one per frame. No segment-aggregation fallback needed. |
| `ProfilerRecorder` counters | **All 13 valid** in the editor (Render *and* Memory). Still to check in a build. |

Baseline scene cost, for context: 488 draw calls, 150 batches, 68 SetPass, 2.10 M
triangles, 1.18 M vertices, 177 shadow casters. Memory: 3.4 GB total used, 2.0 GB
graphics. **27.6 KB GC allocated per frame** — worth chasing down later; some may be editor.

### The finding that shapes RQ2

**The testbed is CPU-bound: GPU 1.88 ms against CPU 8.42 ms.**

This matters more than any other number here. If whole-frame time is the headline metric,
swapping the PBR atmosphere for a cheap baseline may show almost **no** difference, because
the CPU is the bottleneck and the GPU has idle headroom. That would be a true statement
about this testbed and a misleading answer to RQ2, which asks about the *rendering* cost of
the technique.

So: **report GPU frame time as the primary metric**, with CPU and wall time as context, and
state the CPU-bound condition explicitly in the methodology. Consider also reporting at a
higher resolution, where the atmosphere's five full-screen passes shift the balance toward
GPU-bound and the delta becomes visible in whole-frame time too.

Caveat: this is an *editor* run, so CPU includes editor overhead. Re-measure in a
standalone build before drawing conclusions about the ratio.

---

## 2026-08-09 — Country hover highlight and name labels

Branch `country-ui`. Grand-strategy presentation features, not thesis-critical rendering,
but they matter for the strategy-game framing and will appear in report screenshots.

**All three components live on `Game/World/Country Interaction`, so disabling that one
GameObject turns the whole country UI off** — required for measurement runs, since all
three update every frame.

### Hover highlight
`GlobePicker` → analytic ray/sphere against the globe (there are no colliders anywhere),
then `WorldLookup`'s country-index query. `CountryHighlight` builds line geometry for the
hovered country from `Country.shape` and draws it at constant screen-space width.

The baked outline mesh **could not be reused**: `Outline Meshes.bytes` is 24 *spatially*
grouped meshes and the country each border belonged to is discarded during generation, so
there was nothing to tint. Highlighting had to build its own geometry.

Three things that are load-bearing and non-obvious:
- Segments are built in **3D**, which makes the antimeridian a non-issue — ±179.9° become
  nearby points. A lon/lat-space builder would draw a stripe across the map.
- Drawn `ZTest Always`, because terrain spans 150–153.5 and no fixed radius avoids
  mountains. The cost is that the far side would ghost through the planet, so both shaders
  **cull per segment in the vertex shader** (`dot(p, camPos) > R²`), testing the
  *sea-level* points since that test assumes `|p| = R`.
- The glow is displaced to terrain height. Measured from the baked mesh: 25.5% of its
  337k vertices sit above r=150.05, up to 153.17. Left at sea level it drifts by
  `dr/tan(elevation)` — 1.3 units looking straight down, ~20 at a grazing angle.

The hovered country's **terrain is also brightened**, in `Terrain.shader`, reusing the
`texCoord` it already computes. Behind a `COUNTRY_HIGHLIGHT_ON` keyword rather than a
branch, so the extra texture fetch is compiled out when the country UI is off — that
shader is in the path the thesis measures, so the off state has to be genuinely free. The
keyword toggles once on enable/disable, not per hover (swapping variants on every cursor
move risks a hitch); index `-1` is the "nothing highlighted" state. Both the keyword and
the globals outlive the component, so they are cleared on disable and destroy.

Note the fill is applied *before* the atmosphere's tone mapping, so it reads more subtly
at low sun angles than at midday.

### Labels
`CountryLabelData` holds a baked **pole of inaccessibility** per country plus the angular
radius of the inscribed circle there. A centroid is not sufficient — for a concave country
it lands outside the shape entirely, which would put Chile's label in Argentina.

Baked in 3D unit vectors (removes the antimeridian seam *and* the pole singularity),
largest polygon chosen by true spherical area (not point count — coastline detail varies
wildly, which is what keeps the USA's label off Alaska), gnomonic projection so planar
point-in-polygon is valid, scored by **great-circle** distance because gnomonic distorts
scale by 1/cos²θ radially.

`CountryLabelSystem` renders world-space TMP at those anchors, sized from the angular
radius, faded by projected on-screen size and by a horizon test. Uses TMP's
**Distance Field Overlay** shader — the plain one declares `ZTest [unity_GUIZTestMode]`,
a global set by Canvas rendering, and there is no Canvas in this scene.

Idle labels are dimmed and semi-transparent so the map reads first; the hovered country's
label grows, goes fully opaque and turns white, and ignores the size filter since the
cursor has already established which country is meant. Colour and scale are only written
when they change, since `TMP_Text.color` dirties vertex colours.

`flipFacing` is on: TMP's glyphs read correctly from their local −Z, so the object's
forward points into the globe. Determined by looking at it, not by reasoning about TMP's
winding.

### Validation
`LookupProbe` (on `World Lookup`) has three tests, run from its context menu.
**`runOnStart` is off by default** — the sweeps issue ~460 blocking GPU readbacks. Re-run
them after regenerating the index map or re-baking anchors.

Results: index map 8192×4096 R8_UNorm Point in Gamma space; capital sweep 188/192 = 97.9%
of on-land samples (misses are Nicosia and Jerusalem, disputed borders); anchor
cross-check **100% of map-resolvable countries**.

Nine countries — Vatican, San Marino, Anguilla, Bermuda and similar — are smaller than one
texel of the index map (0.0439°, ~4.9 km at the equator). They cannot be hovered and their
labels would be 0.03–0.16 world units wide against Russia's 94, so the size filter never
shows them. Not fixable without a higher-resolution index map, and not worth it.

### Cost to record in the thesis
- `Country Data.asset` is **9 MB and was not previously loaded by `Game.unity`** — the
  country UI adds it to scene load time and memory for a scene the thesis measures.
- `Terrain.shader` gained a `COUNTRY_HIGHLIGHT_ON` variant, doubling its variant count.
  The off state costs nothing at runtime (the fetch is compiled out), but the extra
  variant is a compile-time and build-size cost worth stating if shader counts come up.
- **Measurement runs should disable `Game/World/Country Interaction`.** That clears the
  terrain keyword and stops all three per-frame updates in one go.

### A trap worth remembering
Unity keeps serialized values when a script's defaults change, so retuning a default never
reaches a component already in the scene. This bit the highlight (a width tuned for one
shading model driving another, leaving a 1px hairline). `CountryHighlight` now has a
**Reset Appearance** context menu that reapplies appearance defaults without clearing
references, which `Component > Reset` would.

---

### Deliberately kept, despite looking deletable
- **`Editor Helper/` the folder.** Only `BuildReadyTest.cs` was removed;
  `EditorShaderHelper.cs` sits beside it and `AtmosphereEffect.cs:394` uses it.
- **`WorldLookup`** — idles after `Init`, but is the only way to query terrain height at
  a coordinate, which the harness needs to place a camera a fixed distance above ground.
  `GetTerrainInfoImmediate` does a blocking `GetData`; call it from setup, never per-frame.
- **`PlaceholderWorld`** (`Editor Only / Test Earth`) — deactivates itself on play, and is
  the only thing showing where the planet is in the Scene view before terrain loads.
- **`Country Outlines`** — kept for now as a second, structurally different mesh workload
  to measure against. Drop it if it turns out to be noise.

### Remaining tidy-ups (all optional, none blocking)
- `Assets/Graphics/{Boat Test, Flags, Game Icon}` — verified to have no external
  references. ~3.9 MB. `Game Icon` is wired into Player Settings as the app icon, so
  clear that field first if removing it.
- `Assets/Scenes/Game.unity` still holds two orphaned serialized values on
  `SolarSystemManager` (`player`, `fastForwardDayDuration`). Unity drops them on the next
  scene save.
- `activeInputHandler` is still `2` (Both). Leave it unless the Input System package is
  being removed, which requires setting it to `0` and restarting the Editor *first*.

### Open questions
- Whether the visual comparison stays informal or becomes a real user study. `2afc-wiki`
  (two-alternative forced choice) is in `references.bib` but cited nowhere.
- `Country Outlines` is kept for now — a second, structurally different mesh workload may
  be useful to measure against, but it may equally just be noise.

### Carried forward
- `TestbedCamera.SetView` / `SnapAndSettle` and `SolarSystemManager.SetTimes` are the two
  hooks the measurement harness (milestone 3) will drive.
- The atmosphere's deviations from the pre-study theory are listed in `START.md` §3 and in
  the root `CLAUDE.md`. That list is the seed of RQ3's answer — every item is either a
  finding or a defect, and it will not be reconstructable later.

---

## Session — benchmark harness: screenshots (milestone 3, step 8)

### Timing and capture are separate runs, by design
`BenchmarkRunner.mode` is `Timing` or `Capture`, and only a capture run writes PNGs.

`ScreenCapture.CaptureScreenshotAsTexture` forces a full GPU-to-CPU readback. That stalls
the frame it is taken on and the one after it, so capturing inside a timing run would
corrupt the exact frames a figure is meant to illustrate — typically the twilight and
horizon frames, which are the expensive ones and therefore the interesting ones.

Splitting them is only legitimate because **world state at plan index `i` is a pure
function of `i`**: the plan is fully resolved before the first frame renders and replayed
identically per pass, so the image captured at frame N *is* the image the timing run
rendered at frame N. The evidence for that claim is `plan_hash` and `pose_hash`, which both
runs record — **check they agree before putting a figure next to a number.** `scene_hash`
should also match between a timing and a capture run of the same benchmark, since a
readback changes no geometry.

Consequences, all enforced in code:
- A capture run writes `measured = 0` on every row and produces no segment statistics.
- `authoritative` is false for a capture run regardless of editor or build.
- Repeats are ignored (they would rewrite byte-identical images).
- The run folder is suffixed `_capture` so it cannot be confused at a glance.
- A capture run over a benchmark with no marked frames is refused rather than replaying
  the whole plan to produce nothing.

`superSize` is fixed at 1 and must stay there: a supersized capture re-renders at a
different resolution, which changes both FXAA and the atmosphere's per-pixel cost, so it
would no longer be the image that was measured.

### Prewarm looks like a bug and is not
The visible burst of camera movement before a benchmark settles is the prewarm phase
stepping a decimated sample of the run's own poses (`content/prewarmPosesEvery` frames,
~0.5 s for a 2400-frame run). It exists because the project has no `ShaderVariantCollection`,
so D3D11 pipeline-state creation would otherwise land inside the measured window. Those
frames are `phase = Prewarm` and never enter statistics. `prewarmPosesEvery = 0` disables it.

### Screenshot frames on the example benchmarks
- `daycycle` — first and last of each sun sweep (6 images). The RQ1 set: the sun moves, so
  the endpoints differ.
- `framing` — one mid-hold image per pitch (4). *Not* first-and-last: a hold's endpoints are
  the same pose, which would capture the same picture twice.
- `orbit` — every 90° of each sweep (8), for sun-relative framings where the Mie forward
  lobe shows.
- `smoke`, `altitude` — none.

`screenshots/manifest.csv` records frame index, pass, segment, resolution, camera pose,
sky fraction and **sun elevation in degrees** — the last is there because it is the caption
a twilight figure needs and reconstructing it from `dayT` afterwards is painful.

### Self-check mode — where the noise floor comes from
`BenchmarkRunMode.SelfCheck` replays each profile at least twice in one process (repeats
are forced to ≥2) and writes `selfcheck.md` alongside the usual outputs.

Two kinds of claim, kept deliberately separate:
- **Comparability — pass/fail.** Pose hash identical across every pass; scene hash identical
  *within* each profile (across profiles it may legitimately differ, by the passes each
  adds). A failure here invalidates the spread below, so it is stated first.
- **Spread — reported, not judged.** Max-minus-min of GPU median, p99 and 1% low across
  repeats, per profile and segment. There is deliberately **no threshold** in the report,
  because what counts as acceptable depends entirely on the size of the effect measured.

The headline is the largest median spread across all segments. Read it as: a difference
between two renderer configurations smaller than that cannot be distinguished from
run-to-run variation on this machine.

For calibration, the editor numbers from the earlier two-repeat run were 0.982 / 0.990 ms
median → **0.008 ms spread (0.81%)**, against a measured atmosphere delta of 0.293–0.314 ms
— roughly **37×** the noise floor, comfortably resolvable. That editor figure is optimistic;
re-run in a standalone build before quoting anything, and re-measure whenever the hardware,
driver or scene changes.

p99 and 1% low columns are typically several times wider than the median column — tail
statistics are inherently less stable, so a tail difference needs a correspondingly larger
margin before it means anything. That is why all three are reported rather than just the
median.

### First real self-check: an editor stall, and what it changed
`orbit`, 4 passes (pbr/noatmo × 2 repeats), editor. Noise floor **0.030 ms** (1.81%) on the
worst segment, against a measured atmosphere delta of ~0.570 ms — about **19×**, comfortably
resolvable. Editor figure; re-measure in a build before quoting.

The interesting part was a **FAIL on scene hash**. Cause, from `frames.csv`:

| frame | wall_ms | gc_alloc | draw calls | triangles | gpu_ms |
|---|---|---|---|---|---|
| 356 | **1002.36** | 10 KB | 407 | 2.19 M | 1.86 |
| 357 | 59.15 | **4.3 MB** | 407 | 2.19 M | 2.01 |
| 358 | 33.51 | 417 KB | **2443** | **13.15 M** | **0** |

A one-second main-thread stall in the editor (nothing in the harness can block that long),
then a single frame whose profiler counters absorbed roughly six frames' worth of geometry
and whose GPU timing was invalid. **1 differing frame out of 4800 compared pairs**;
`noatmo_r0` vs `noatmo_r1` were byte-identical throughout.

So the check was right that the hashes differed and wrong about what it meant. A binary
hash comparison cannot distinguish one stall from a systematic difference, and it declared
a perfectly usable noise floor invalid.

**Fix:** `PassResult` now retains per-measured-frame geometry (draw calls, triangles, LOD
count, wall ms), and the self-check reports *how many* frames disagreed rather than only
that the hash did. Under 0.5% differing → `PASS (isolated stalls)`, with the run's worst
wall-clock frame quoted next to the median so the stall is visible. At or above that →
`FAIL`, described as systematic. Both cases print the actual counts; the threshold only
picks the wording.

Consequence for the protocol: **an editor self-check will occasionally eat a stall.** The
median is robust to it, the p99 and 1% low are not — the wide `pbr / alps` p99 spread
(1.381 ms vs 0.112 ms on the neighbouring segment) is that one frame. Another reason the
quotable noise floor has to come from a standalone build.

### Standalone build path (milestone 3, step 10)
Editor runs are never authoritative, so every number in the report has to come from a build.
`Testbed → Benchmark → Build Standalone Player` makes one, as a **development build** —
Unity strips the profiler from release players and the Render counters the harness records
are among the casualties. The development flag's CPU overhead applies identically to every
profile, so a like-for-like delta survives it; a missing counter does not.

**`BuildStamp`** bakes the commit into `Assets/Resources` at build time. A player has no
`.git` beside the executable and may be on a machine without git, so `GitInfo` cannot shell
out the way it does in the editor — without this every build's `run.json` would record
commit `unknown`. The asset is **gitignored**: it changes on every build, and committing it
would dirty the tree, which would make the next stamp report dirty for no reason. The build
prompts for confirmation if the tree is already dirty.

**Command line** (all optional; anything not passed keeps the scene-authored value, so one
build serves scripted and interactive use):

```
Atmos.exe -benchmark framing -mode selfcheck -resolution 1920x1080 \
          -machine "desktop" -strict -quitWhenDone
```

`-benchmark <id>` selects from the runner's **`availableBenchmarks`** list — populate it in
the inspector, because a ScriptableObject only reaches a build if something references it,
and an unlisted definition would exist in the editor and silently not exist in the player.

Exit codes: `0` ok, `1` run failed (frame-count mismatch, pose-hash mismatch, abort),
`2` could not start (bad options, no benchmark, batch mode), `3` `-strict` violation (GPU
timing unavailable).

> **Never run the player with `-batchmode`.** `WaitForEndOfFrame` never resumes there, so
> the end-of-frame reader never runs, `FrameCursor` never advances, and the run hangs
> forever rather than failing. The runner now detects batch mode and refuses to start —
> this was the single most likely way to wedge a scripted run.

### In-application benchmark control (`BenchmarkHud`)
Additional to the command line, not a replacement — scripted runs want
`-benchmark ... -quitWhenDone`, driving it by hand wants to see the selection and press a
key. Both go through the same `BenchmarkRunner`.

| key | action |
|---|---|
| `F2` | cycle benchmark, wrapping through **All** past the last entry |
| `F3` | cycle mode (Timing → Capture → SelfCheck) |
| `F4` | run the current selection |
| `Esc` | abort a run in progress, clearing any queue |
| `F6` | hide/show the overlay |

Sits bottom-left so it does not fight the time overlay top-left. Shows the selected
benchmark, mode, profile list and the **size of the run** — total frames × passes × queued
runs — computed by `BenchmarkPlan.EstimateLength`, which mirrors `Build`'s assembly without
resolving views or solving sun positions. Validated against all seven runs on disk
(daycycle 2078, altitude 2066, orbit 2681): exact match.

**The overlay hides itself while a run is in progress.** IMGUI draws into the same
backbuffer as everything else, so it would appear in every captured screenshot and add its
own draw calls to the counters being recorded. `showProgressDuringRun` opts back in for
debugging, and is ignored in Capture mode regardless.

**All** queues every available benchmark as separate runs — they have different plans, so
they cannot share one, and each gets its own output folder. If `StartRun` refuses one (most
likely a capture run over a benchmark marking no frames), the queue logs it and skips to the
next rather than stalling on the bad entry.

### Batch runs (All)
Selecting **All** now writes one parent folder, `<stamp>_batch_<commit>/`, containing each
benchmark's own run folder plus two cross-benchmark files. Runs still get their own folders
— different benchmarks have different plans and frame counts, so they cannot share one; the
batch groups them and adds a view across them.

- **`batch-summary.md`** — one row per (benchmark, segment): each profile's median GPU
  frame time and its delta from the baseline. The baseline is the **first entry in the
  runner's `profiles` array**, so reorder that array to change what everything is compared
  against.
- **`batch-summary.csv`** — every segment of every run with `benchmark`, `mode`, `pass_id`,
  `profile` and `repeat` columns prepended to the full statistic set. This is the file a
  plotting script wants; assembling it from the per-run folder layout by hand is tedious.

Rendered from the real runs on disk, the shape is:

| benchmark | segment | sky frac | noatmo | pbr | delta pbr |
|---|---|---|---|---|---|
| daycycle | daylight | 0.77 | 0.749 | 1.127 | +0.379 |
| daycycle | twilight | 0.77 | 0.764 | 1.126 | +0.363 |
| orbit | alps | 0.26 | 1.072 | 1.642 | +0.570 |
| orbit | sahara | 0.26 | 1.070 | 1.626 | +0.556 |

Note the atmosphere costs *more* in the low-sky-fraction views (+0.57 ms at 0.26) than in
the high-sky ones (+0.38 ms at 0.77). That is the expected direction — the sky raymarch
early-outs on rays that miss the atmosphere, while the aerial-perspective composite runs on
terrain pixels — but **these two benchmarks differ in altitude and LOD count as well as
framing**, so it is not a clean attribution. `framing` is the benchmark that isolates it:
four holds from one position varying only pitch.

An aborted batch still writes a summary covering the runs that completed, with a warning
saying how many of how many it covers. The output-root redirect is restored on every exit
path, so a later single run never writes into a stale batch folder.
