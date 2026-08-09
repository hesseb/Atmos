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

### Default resolution raised to 1440p
There were two separate sources of 1080p, and both are now 2560x1440:

- **Player Settings** — `defaultScreenWidth/Height` 2560x1440, `defaultIsNativeResolution: 0`
  (it was 1, which ignored the default and used the display's native size), and
  `fullscreenMode` 2 → **3 (Windowed)**. That last one is required: MaximizedWindow and
  FullScreenWindow both ignore the requested size and take the display's, so an exact,
  known pixel count is only achievable windowed. `resizableWindow` stays 0, so the window
  cannot be dragged to a different size mid-session.
- **`BenchmarkRunner.targetResolution`** — scene value and C# default both 2560x1440, so
  pressing run no longer resizes the window from 1440p down to 1080p.

1440p is **1.78x** the pixels of 1080p. The atmosphere is five full-screen passes, so its
cost scales roughly with pixel count — results at the two resolutions are not comparable.
No loss here: every run on disk so far is an editor run, explicitly non-authoritative.
`-resolution WxH` still overrides for scripted runs, and the requested *and actual*
resolution are both recorded in `run.json` with a `matched` flag.

> If the display is smaller than 2560x1440 the window will not fit. Either lower
> `defaultScreenWidth/Height`, or set `fullscreenMode` back to 1/2 and accept the display's
> own resolution as the measurement resolution.

### Release build option, and the stripped-counter trap it exposed
`Testbed → Benchmark → Build Standalone Player (Development | Release)`. Each kind builds
into its own `<product>-<kind>/` subfolder — a player is an exe plus a `_Data` folder and
several DLLs, so building both into one directory would have the second overwrite the
first, and results live beside the exe so they would mix too.

What actually differs:
- **Frame timings survive release.** `FrameTimingManager` is not a development-build
  feature; it needs `enableFrameTimingStats`, which this project sets. So the primary RQ2
  metric is available either way.
- **`ProfilerRecorder` counters are the open question.** Unity strips much of the profiler
  from a release player. Build both, run the same benchmark, and diff `counters_available`
  in the two `run.json` files — that turns an assumption into a measured fact, and the
  frame-time difference between them is the cost of the development flag.

**The trap:** when the counters are stripped they read **zero**, not absent. Three checks
would have read that as agreement:
1. The self-check's geometry comparison — every frame zero, every pass identical, confident
   `PASS` having compared nothing.
2. `scene_hash` — partially degraded rather than destroyed, since `lod_high_res` comes from
   `SimpleLodSystem` and is unaffected. Two profiles drawing genuinely different geometry
   could still share a hash.
3. Nothing in `warnings[]` said the counters were missing.

All three now handle it: a new `COUNTERS_UNAVAILABLE:<n>/<total>` warning, the geometry
verdict reports `n/a (profiler counters unavailable)` instead of PASS, and both
`selfcheck.md` and `summary.md` carry an explicit banner saying a match is not evidence.

This is the general shape of the hazard the plan flagged as "empty cells rather than zeros":
a reader must never mistake *stripped* for *zero*. `frames.csv` already got that right; the
derived checks did not.

---

## First authoritative results: development vs release, 1440p, RTX 4090

Full suite (5 benchmarks, pbr + noatmo, 2 repeats = 4 passes each) run from both builds at
commit `2bcb572`. Both report `authoritative: true`, `resolution 2560x1440 matched`,
`frame_timing_available`, GPU timing lag 1 frame.

### 1. Release keeps almost all instrumentation — earlier claim was wrong

I had said the Render profiler counters do not survive a release player. **They do.**
**11 of 13 counters are available in release**; only two are stripped:

- `GC Allocated In Frame`
- `Gfx Used Memory`

Every Render counter survives — draw calls, batches, SetPass, triangles, vertices, shadow
casters — so `scene_hash`, the geometry-agreement check and the self-check all work fully in
a release build. The defensive `countersPresent` handling added for stripped counters never
triggers here; keep it anyway, it is cheap and the assumption was wrong once already.

**Consequence: release is the better build for the thesis.** Nothing an RQ metric depends on
is lost. Use development only when per-frame GC allocation matters — which is how the
editor stall got diagnosed, so it is not worthless.

### 2. The development flag costs essentially nothing on GPU time

Across 26 segment/profile pairs: **median +0.0023 ms, max +0.0276 ms, min −0.0038 ms**.
Several are negative. This is noise, not overhead — expected, since the flag is CPU-side and
the metric is GPU. The build choice can be made on instrumentation grounds alone.

### 3. `daycycle` pose hash differs across builds — diagnosed, not a defect

`daycycle` is the only benchmark whose `pose_hash` differs between dev and release. Cause:

- The **plans are byte-identical** (`plan_hash` matches, and a full column diff of `plan.csv`
  shows zero differences), so the intent was identical.
- The **camera is bit-identical** in all five benchmarks.
- The **sun direction** differs by at most **1.3e-6** — a last-bit floating-point difference
  in the runtime sun transform between build configurations.
- `MixQuantized` rounds at scale **10000** (1e-4 for every component, including directions —
  note the plan document said 1e-3 for angles; the code does not). Exactly **3 of 6234**
  sampled components sit within 1.3e-6 of a rounding boundary and flip buckets. First is row
  473, `sun_dir_z` −0.56155038 vs −0.56154990, which straddles the .5 midpoint at ×10000.

Only `daycycle` is affected because it is the only benchmark that **moves** the sun: 1800
measured frames of sun motion is 1800 chances to land on a boundary, whereas the others hold
it at a single solved value.

**Within** each build all passes agree, in both builds — so the hash does its actual job.
Cross-build it is brittle by construction: any quantisation has boundaries. **Rule: compare
`pose_hash` within a build; across builds compare with a tolerance** (the procedure is a
column diff of `frames.csv` on `cam_*`/`sun_dir_*`, which is what produced the numbers here).

### 4. Atmosphere cost is not monotonic in sky fraction

Release build, absolute GPU cost of the atmosphere (pbr − noatmo), by sky fraction:

| benchmark | segment | sky | pbr | noatmo | cost ms | cost % |
|---|---|---|---|---|---|---|
| framing | nadir | 0.00 | 1.223 | 1.017 | +0.205 | 20.2% |
| framing | steep | 0.05 | 1.277 | 1.066 | +0.211 | 19.8% |
| framing | oblique | 0.54 | 1.046 | 0.806 | **+0.240** | 29.8% |
| framing | horizon | 0.97 | 0.439 | 0.281 | +0.158 | 56.4% |

`framing` is the clean comparison — one position, only pitch varies. Cost **peaks at
intermediate sky fraction**, which is the expected shape: at nadir you pay aerial perspective
on nearly every pixel and almost no sky raymarch; at the horizon nearly all sky raymarch and
almost no aerial perspective; in between you pay both.

Note absolute and relative cost tell **opposite** stories — relative cost climbs
monotonically (20% → 56%) only because the baseline falls faster, terrain being the expensive
thing. **Report absolute ms for RQ2**; the percentage is a statement about the terrain
renderer, not the atmosphere.

> **Caveat: no self-check was run on these builds.** The differences between segments
> (0.035–0.082 ms) are close to the editor noise floor of 0.030 ms, and the real 1440p
> release noise floor is unmeasured. The non-monotonicity claim needs a self-check before it
> goes in the report.

### 5. `TIMING_ATTRIBUTION_ANOMALIES:1` on every run, both builds
One frame per run where the timing stream did not deliver exactly one new timing. Consistent
across all ten runs, so it is a property of the instrumentation rather than of any benchmark.
One frame in 400–2400 is not material, but it should be explained before the report rather
than left as an unexamined warning.

### `TIMING_ATTRIBUTION_ANOMALIES:1` explained and fixed
It was a false positive on the **priming call**, and it fired on every run ever recorded.

`FrameSampler.Capture` counts a timing as fresh by comparing `cpuTimePresentCalled` against
`lastSeenPresentTime`. On the first call that baseline is still −1, so the comparison is
skipped and *every* timing already sitting in the buffer counts as fresh — `fresh > 1`, one
anomaly, every run.

Verified rather than assumed: the release `orbit` run has **2681 rows and zero frames with
`timing_valid = 0`**, so nothing was ever mis-attributed. The `fresh > 1` check is now
guarded on a baseline existing.

Worth stating as a principle: a warning that fires on every run is worse than no warning,
because it teaches the reader to skip the field that is supposed to mean something. The
existing results remain valid — the flag was noise, and the frame data behind it was correct.

### `FrameProbe` is retained, not retired
Originally scoped as a temporary diagnostic. Keeping it, for two reasons:
- It is inert. `runOnStart: 0` in the scene and `Update` returns immediately while Idle, so
  it costs one branch per frame and only runs from its context menu.
- Its findings in this file are marked "do not re-derive" — but after a Unity upgrade they
  *would* need re-deriving, and this is the tool that derives them.

Removing it would also mean deleting a live component from `Game.unity` by hand, which is a
worse trade than keeping an inert script.

---

## Milestone 4: the baseline renderer

### `noatmo` was never a baseline
The camera clears to solid black (`m_ClearFlags: 2`) and `RenderSettings.skybox` is the stock
Default-Skybox that is never drawn, so "atmosphere off" renders a **black sky** and terrain
with no distance haze. Every RQ2 number recorded before this milestone is "PBR minus
nothing", not "PBR minus a cheap alternative". `noatmo` is kept, but as an **ablation
control** and labelled as one.

The physically based path also does two jobs — sky radiance and aerial perspective — so the
baseline has to do both or the comparison is two features against one.

### Where the sky is drawn, and why not in the post chain
Every sky variant renders from a CommandBuffer at `BeforeForwardOpaque`, through one shared
`SkyPass.Record`. Making the baseline a `PostProcessingEffect` instead would have integrated
with the profile system for free, but it would have confounded the shading model with **four**
simultaneous differences, all favouring the baseline: pass slot, blit count (1 vs 2), a
possible MSAA resolve the pre-opaque slot pays, and depth rejection. One full-screen RGBA16F
read+write at 2560x1440 is roughly 0.06–0.09 ms against a measured atmosphere cost of
0.158–0.240 ms — the confound is the same size as the signal.

Depth rejection is available in **both** slots (`PostProcessingManager` sets
`depthTextureMode = Depth`, forcing a prepass, and `AfterDepthTexture` precedes
`BeforeForwardOpaque`). So "the PBR sky wastes work on covered pixels" is not a property of
the slot — it is an optimization `DrawSky` does not do. Applying it to one arm only would be
a measurement error; applying it to both and measuring is Stage 6.

`RenderSettings.skybox` + `clearFlags = Skybox` was rejected outright: the built-in skybox
draws after opaque with `ZTest LEqual` and `Star.shader` is `ZWrite Off`, so **every star
would be painted over** while the moon survives (it writes depth) — a moon in an empty sky.

### The tone-map pedestal
The gradient generator authors anchors as **the colour wanted on screen** and inverts the
tone map to find what to store. Authoring stored values directly does not work: `toneMap`
applies `lerp(0.5, lum, 1.45)`, which has a pedestal at 0.155, so every plausible night-sky
radiance lands on the `smoothMax` floor and comes out flat black — leaving the stars nothing
to sit against — while daylight clips past 1. **The usable input band is roughly [0.17, 0.87]**,
which is not a range anyone would guess; the first set of hand-picked anchors was an order of
magnitude out at both ends. The inverse round-trips to 2e-16 and reads the tone-map constants
off the scene renderer rather than assuming them.

### Profiles: decomposition, not just A/B
| profile | Atmosphere | Aerial | sky |
|---|---|---|---|
| `pbr` | on | off | PBR |
| `baseline-gradient` | off | on | hand-authored LUT |
| `baseline-baked` | off | on | LUT baked from PBR |
| `baseline-cubemap` | off | on | static cubemap |
| `nullsky` | off | off | pass with no shading |
| `noatmo` | off | off | none |

`nullsky − noatmo` is the pass structure alone; `baseline − nullsky` the shading model;
`pbr − baseline` the headline number. `baseline-gradient` vs `baseline-baked` share a shader
path and a cost exactly, so any visual difference between them is **purely** authoring method
— which is the cleanest authoring-flexibility evidence the report can get.

Every profile states every switch explicitly. An omitted toggle inherits whatever the previous
pass left behind.

### Baked LUT: two structural limitations, both findings
- **No azimuth axis.** Each texel is the mean radiance over all horizontal directions, so the
  Mie forward lobe is absent by construction. The shader's separate glow term stands in for it.
- **No altitude axis.** Baked at one observer height (12, matching the benchmark cameras) and
  progressively wrong away from it. A strategy-game camera ranges over a large fraction of the
  atmosphere's thickness, so this is a real limit of the technique rather than of this
  implementation.

The bake includes `AtmosphereCommon.hlsl` and calls the same `raymarch()` the runtime sky
does, so a difference between baked and physically based cannot be a difference in the bake.

### First baseline measurement — and the premise does not hold
`smoke`, editor, 2560x1440, RTX 4090, 6 profiles x 2 repeats. Plumbing verified:
`nullsky - noatmo` is **exactly +2 draw calls**, all 12 passes share one pose hash, scene
hashes group 425 / 427 / 428 exactly as the pass counts predict, and `run.json` records the
*live* sky mode per profile. The three baseline variants land within **0.003 ms** of each
other, which is the expected consequence of their sharing a code path.

| | ms |
|---|---|
| sky pass structure (`nullsky − noatmo`) | +0.051 |
| baseline shading + aerial (`baseline − nullsky`) | +0.162 |
| **PBR over baseline** | **+0.073** |
| whole sky + aerial (`pbr − noatmo`) | +0.286 |

**The cheap baseline captures 74% of the physically based renderer's cost**, and the delta is
7x the worst repeat spread (0.010 ms), so it is not noise.

The reason is structural: Hillaire's method precomputes scattering into a 128x256 LUT, so at
the pixel level the physically based sky is *also* just a texture fetch. Everything else -
two full-screen blits, tone map, dither, star composite, the aerial pass - is common to both.
The report's framing ("physically based versus a cheaper textured method") assumes a cost gap
that this technique largely removes. That is a finding, not a problem, but RQ2's phrasing
should account for it.

Caveat: editor run, dirty tree, and `smoke` has only 200 measured frames per segment, so the
1% low is suppressed (n < 300, working as designed). Re-run on the release build with a
longer benchmark before quoting.

### Two defects this run exposed
1. **`baseline-cubemap` had no cubemap.** `skyCubemap: {fileID: 0}` - the plan called for
   baking one and it was never implemented, so the variant sampled an unbound sampler. Its
   *cost* is still a valid measurement of the code path, but the image is meaningless, so it
   cannot be used for RQ1. The renderer now warns loudly instead of rendering something
   plausible-looking and wrong.
2. **The decomposition was mislabelled.** `nullsky` has no aerial perspective pass (427 draw
   calls) while the baselines do (428), so `baseline − nullsky` bundles the entire aerial pass
   in with the sky shading - 0.162 ms reported as "shading" when the shading alone is a
   fraction of it. Added a `nullsky-aerial` control that is the structural twin of the
   baselines, so `baseline − nullsky-aerial` isolates the sky shading and nothing else.

### F5: live renderer preview
`BenchmarkHud` cycles the runner's profiles live — scene as authored, then each profile in
turn, then back. For looking at the difference rather than measuring it.

It applies through a `RestoreScope`, which is not optional bookkeeping here:
`PostProcessingEffect.enabled` is a serialized field on a ScriptableObject *asset*, so a
preview left applied would be written to the project on the next save and the scene would
quietly come back configured as whichever renderer was last looked at. The scope is unwound
on three paths — cycling to the next profile, starting a run, and `OnDisable`.

Clearing it before a run matters for a second reason: a run pins the environment by
snapshotting current values, so starting one with a preview applied would record the
previewed state as the thing to restore to, and every pass would measure from a perturbed
starting point.

The overlay's `viewing` line reports the live `RenderingManager.ActiveMode` alongside the
requested profile, so a profile that failed to apply cannot claim on screen that it worked.

### Open thread: art-directed haze *on top of* physically based aerial perspective
The doubled-up configuration found by accident — `Atmosphere` (PBR sky + scattering-LUT
aerial perspective) **plus** `AerialPerspectiveSimple` (cheap exponential fog keyed to sun
elevation) — was judged to look **better** than the physically based aerial perspective alone.

Worth taking seriously rather than filing as a bug, because it is an RQ3-shaped result: the
physically based path is correct but not art-directable, and a cheap ramp on top restores
artistic control over distance haze for ~0.05 ms without touching the scattering. That is
exactly the "how can the physically based method be adapted to work practically" question,
and it is a hybrid neither arm of the current comparison represents.

To explore when the PBR profiles get detailed attention:
- Is the improvement the *colour* (art-directed ramp vs derived) or just *more* haze? Test by
  raising the physically based `aerialPerspectiveStrength` alone and comparing.
- The PBR aerial perspective is disabled for Earth shadow at 32³ (`START.md` §3) and the LUT
  is coarse; some of what the cheap fog adds may be covering a resolution artefact.
- If it survives scrutiny it deserves its own profile (`pbr-hybrid`) and a paragraph, since
  "keep the physics, add an art-directed term" is a transferable recommendation.

Defaulted `Aerial Perspective.asset` to `enabled: 0` so the authored scene is plain PBR; this
hybrid needs to be a deliberate profile, not an accident of asset defaults.

### Cubemap bake
`Testbed → Baseline Sky → Bake Sky Cubemap From PBR` renders the six faces through the same
`raymarch()` as everything else and writes `SkyCubemap.asset`. Unlike the gradient it keeps
full directional detail — azimuth variation, the Mie forward lobe, the sun's own glow — which
is what a real skybox has.

Baking a sky model to a cubemap is what studios actually do, so this is a representative
"textured skybox" workflow rather than a shortcut. It is **frozen at 25° sun elevation**;
that is the variant's entire point, and the day-cycle benchmark is where the failure shows.

Face orientation is the known trap: get the convention wrong and the result still looks like
a sky, just with seams. The face bases are checked against the Direct3D reference formulas
(verified exact), and the baker additionally **measures the discontinuity across the +Z/+Y
seam both with and without a vertical row flip and takes whichever is smaller**, logging both
numbers. If both are large the basis is wrong rather than the row order, and the log says so.

### The cubemap seam was not an orientation bug
Visible seams on `baseline-cubemap` turned out not to be face orientation at all.

**A cubemap sampled by raw world direction has its horizon pinned to world +Y.** On a globe
that only lines up with the real horizon at one point on the planet; everywhere else the
baked horizon — a hard planet-occlusion edge — sits at an angle to the real one, and turning
the camera sweeps across it. The gradient variants were unaffected because they derive view
elevation from the observer's local up, which is correct anywhere.

Fixed by sampling in the observer's frame: `up` from the camera and planet centre, plus a
continuous horizontal basis from the planet's axis.

Only the vertical axis has to match the bake. The **azimuth origin is deliberately not
aligned** to the bake's: a static cubemap cannot track the sun's azimuth either, so matching
it would be false precision. The requirement is continuity, verified orthonormal to 0.0 and
continuous to 0.0 across latitude. It is singular at the poles, where the reference falls
back and the azimuth jumps — no benchmark goes there, but a pole flyover would need a better
basis.

The first seam check was also worthless and is replaced: it compared the +Z/+Y seam in the
read-back arrays *before* `SetPixels`, and a vertical flip applied consistently to every face
leaves that comparison unchanged while still producing seams once assembled. The replacement
uses ground truth — we choose the sun's elevation, so it must land on a computable texel of
the +X face (~119 rows apart between the two hypotheses at 256²) — and now reports the verdict
in a **dialog**, not just the Console.

### The cubemap orientation warning was a false alarm
The bake reported "the face basis is wrong" on a cubemap that is correct. The measurement:
brightest texel on +X at row **174.0**, against predictions of 187.2 unflipped and 67.8
flipped. The decision (direct) was never in doubt — 13 rows versus 106 — but the 13-row
residual tripped a tolerance set at 5% of the face (12.8 rows).

Decoding the residual explains it: row 174 implies a sun elevation of **19.97°** when the sun
was placed at **25°**. The check assumed the brightest texel *is* the sun direction, and it is
not. **Air mass grows toward the horizon, so the product of the Mie phase function and the
path integral peaks a few degrees below the sun rather than at it.** A ~5° downward bias is
the physically correct result.

The criteria are now stated in terms that mean something rather than in texels:
- implied sun elevation within 12° of the actual, with the downward bias expected and
  explained in the message
- brightest texel on the face's centre column (within 0.08 of centre) — the bias is purely
  vertical, so a horizontal offset *would* indicate a broken basis

Against the real measurement those give elevation error 5.03° and column offset 0.0000 →
confident. Worth keeping as a lesson: a self-check built on an assumption that is *nearly*
true reports failure on correct output, which costs more than having no check at all.

### The real cubemap bug: planet occlusion baked into a background
The remaining seam — serrated, only at mid zoom, gone near one particular altitude — was
neither orientation nor sampling frame. **Both bakes stopped rays at the planet surface**, so
they contained a hard dark edge at the horizon *as seen from the bake altitude of 12*.

The real horizon moves with altitude, and by a lot:

| camera altitude | horizon below local horizontal | mismatch vs bake |
|---|---|---|
| 4 | −13.1° | 9.1° |
| 12 | −22.2° | 0° |
| 30 (`orbit`) | −33.6° | 11.4° |
| 220 (`altitude`) | −66.1° | 43.9° |

Across the mismatch band the baked texture says *planet* while the scene says *sky*, putting
a hard dark edge in open sky. That accounts for every symptom: serrated (a hard edge in a
256² map, magnified), absent near altitude 12 (no mismatch there), and hidden at the extremes
where terrain covers the band or it leaves the frame.

**A baked sky is a background drawn behind real terrain, so it must contain only sky.**
Downward rays are now folded up onto the horizon, making the lower hemisphere a smooth
continuation of horizon colour — what a skybox actually looks like, and altitude-independent.
Terrain covers it in practice. Applied to the gradient bake too, which had the same defect in
its lower half.

Cubemap also raised to 512² (~12 MB): it is magnified across the whole sky and the horizon
band is where the gradient is steepest.

Marching *through* the planet instead was not an option: `getScatteringValues` takes height as
`|pos| − planetRadius`, so a negative height makes the density exponential blow up.

**Resolved.** Sky-only baking fixed the seam. The chain of three wrong diagnoses is worth
keeping as a record: face orientation (never wrong), sampling frame (wrong, and a real bug,
but not the seam's cause), and finally planet occlusion baked into a background (the actual
cause). The self-check gave a false alarm at every step, because each version rested on an
assumption that was only nearly true.

Consequence for the numbers already taken: the `smoke` timings stand — texture *content* does
not change the cost of a texture fetch — but any RQ1 figure from `baseline-baked` or
`baseline-cubemap` predates the fix and must be recaptured.

---

## Deferred to the optimization phase: depth rejection on the sky pass

**Not done, deliberately.** Recorded here so it is picked up when optimizations get their own
attention rather than being lost.

The sky pass shades every pixel, including those terrain later covers. `PostProcessingManager`
sets `depthTextureMode = Depth` unconditionally, which forces a depth prepass, and
`CameraEvent.AfterDepthTexture` precedes `BeforeForwardOpaque` — so `_CameraDepthTexture` is
already available where the sky is drawn, and rejecting covered pixels costs one sample.

Expected saving scales with the terrain-to-sky ratio, so it is largest exactly where the sky
pass is currently most wasteful: `framing/nadir` at sky fraction 0.00 spends the whole pass on
pixels the terrain overwrites.

**It must be applied to BOTH arms and measured as its own pair of profiles.** Applying it to
the physically based path alone would be a measurement error, not an optimization — and the
temptation is real, because the physically based sky is the one that looks like it needs
optimizing. Applied symmetrically and reported with a number, it is exactly the "named,
justified optimization against a named baseline" that RQ3 asks for and THESIS.md §6.1 requires.

Related and also deferred: `SkyPass.Record` allocates a temporary RT and blits twice, measured
at **0.041 ms**. A single-pass approach is impossible while the sky shaders composite against
`_MainTex.a` (the star/moon brightness channel) with a non-linear blend, but that hack is
flagged "TODO: make it good" in `DrawSky.shader` and replacing it would make one blit viable.
