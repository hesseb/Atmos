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

---

## First authoritative baseline results: release, SelfCheck, 1440p, RTX 4090

Full batch, 7 profiles x 2 repeats, all five benchmarks. Pose hash and geometry **PASS** on
every benchmark. **Noise floor 0.029 ms worst case** (a single outlier on
`baseline-baked/nadir`); typical run-to-run spread is **0.000–0.004 ms**.

### The cost decomposition, at last measured cleanly

| component | mean | how |
|---|---|---|
| sky pass structure | **0.041 ms** | `nullsky − noatmo` — two blits and a temp RT |
| cheap aerial perspective | **0.116 ms** | `nullsky-aerial − nullsky` |
| baseline sky shading | **0.050 ms** | `baseline-gradient − nullsky-aerial` |
| physically based over baseline | **~0.00 ms** | `pbr − baseline-gradient` |

All three components are strikingly consistent across 13 segments spanning sky fractions from
0.00 to 0.97.

### The headline: the two are indistinguishable in cost
`pbr − baseline-gradient` averages **+0.001 ms** below sky fraction 0.30 and **+0.005 ms**
above 0.70 — both inside the noise floor. Per segment it ranges −0.037 to +0.062, with one
outlier at −0.113 (`altitude/descend`).

At 1440p on a 4090, **a physically based sky costs the same as a textured one.** Hillaire's
method precomputes scattering into a 128×256 LUT, so per pixel both are a texture fetch; the
runtime cost is the pass that carries them, not the model inside.

That reframes RQ2. The question "what does physical accuracy cost" has the answer "nothing
measurable here", which makes RQ1 and RQ3 carry the thesis and is a stronger result than a
trade-off curve would have been. It needs stating carefully: it is one resolution on one very
fast GPU, and the LUT sizes are fixed — the cost would reappear at higher LUT resolutions,
with multiple scattering, or on hardware where 3.7 M pixels of texture fetch is not free.

### The biggest single cost is not the sky
The cheap aerial perspective pass (0.116 ms) costs **more than twice** the sky shading
(0.050 ms) and nearly three times the pass structure (0.041 ms). It computes one `exp()`. The
cost is a full-screen read-modify-write at 1440p, not arithmetic — which says the interesting
optimizations here are about pass count and bandwidth, not about the scattering model.

### Bug this exposed: the cheap fog was being applied to the sky
`AerialPerspectiveSimple` had no sky exclusion, so at the far plane the exponential left only
40% of the sky and **replaced 60% of it with the haze colour**. The physically based pass
skips sky pixels explicitly. So the two were doing different jobs and the difference was being
attributed to the technique. Fixed; **the batch above needs re-running**, and the baseline's
cost should fall at high sky fractions where it was previously doing needless work.

### Outlier to investigate
`altitude/descend` is the only segment where the physically based path is dramatically cheaper
(−0.113 ms), and `daycycle` at the same nominal sky fraction shows the opposite sign (+0.058).
The mean sky fraction of a segment that sweeps altitude 220 → 4 is not a meaningful summary of
it, so this is probably an artefact of averaging over a huge altitude range rather than a real
effect — but it should be checked before anything is claimed about sky fraction.

### The tone map's pedestal is a threshold, and it was hiding behind the fog
Removing the fog-over-sky bug revealed two defects it had been masking — it was replacing 60%
of the sky with haze colour, which papers over a dark sky and softens seams.

**Root cause of both: `toneMap` applies `lerp(0.5, lum, 1.45)`, whose zero crossing is at
0.155.** That is not a curve near black, it is a *threshold*: anything below crushes to the
floor, so a value that is slightly too dark does not render dim, it renders **absent**, and a
smooth gradient crossing 0.155 becomes a hard edge.

Two consequences, both now fixed:

1. **Tone-map constants did not match.** `AtmosphereEffect` uses intensity 1.31, whitePoint 1,
   dither 4; `BaselineSkyRenderer`'s C# defaults were guesses at 1, 1.1, 0.8. Scene values
   corrected. Note the hand-authored gradient is inverse-mapped against these constants, so
   **it must be regenerated after any change to them.**

2. **Azimuth averaging removed sunsets entirely.** The bake averaged 32 azimuths. At low sun
   the sky is bright only *toward* the sun, so the mean fell under the pedestal and sunset
   went black, while noon survived because the sky is near-uniform there. Measured from the
   baked EXR: at sunset the horizon stored 0.182 and the zenith 0.040, against a threshold of
   0.155 (0.118 once intensity is corrected) — so the whole sky bar a sliver of horizon was
   below it.

   Now sampled at a **fixed azimuth measured from the sun**, defaulting to 0 (sunward). That
   keeps the colours that make a sunset legible; the cost is an anti-solar sky rendered too
   warm. That trade is the honest limitation of having no azimuth axis, and which way it was
   taken is what needs stating.

The cubemap seams are the same story: it has full directional detail and needs no averaging,
but with the sky sitting near 0.155 the pedestal turned every small cross-face difference into
a hard black/not-black edge. Correcting the exposure should take most of it out.

> Debugging note: the EXRs are uncompressed float, so they can be parsed directly to read the
> baked values. That is how this was settled rather than guessed — and worth remembering,
> since two earlier hypotheses (face orientation, sampling frame) were wrong.

### Cubemap orientation, third check: continuity through the real lookup
The sun-differencing check locates the sun accurately (23.3° measured against 25° actual,
column dead centre) but it still could not see the seam, because **it reads the face arrays
and never the assembled cube**. A flip applied uniformly to every face leaves array-space
comparisons unchanged while still breaking the cube.

The deciding test now walks a great-circle arc from 30° to 60° elevation — crossing the +Z/+Y
boundary at 45°, verified — sampling through the **Direct3D cube lookup in C#** and measuring
the largest step between neighbouring samples. Going through the lookup is what makes it
work: a uniform flip changes which texel a direction resolves to, so it breaks continuity at
horizontal edges, which is exactly the seam. Both orientations are measured and the smoother
one wins; if it disagrees with the sun test, continuity wins and says so.

That is three checks on one property, and the lesson is consistent: **each earlier one tested
a proxy rather than the thing that fails.** Array agreement, then sun position, then finally
the assembled cube sampled the way the GPU samples it.

> `baseline-cubemap` showing no sunset is **not** a defect. It is frozen at 25° sun elevation
> and cannot track the sun; that is the failure mode the variant exists to demonstrate. Only
> the gradient variants respond to time of day.

### The cubemap seam is a tone-map contour, not geometry — variant parked
Both orientation checks now agree and continuity is decisive: **direct 0.6332 vs flipped
0.0028**, a 226x difference. The assembly is correct, and it was already flipped in the
previous bake — so the cubemap being looked at has been correctly assembled throughout.

The seam is the shared tone map crushing the darker sky to black with a hard edge.

**`toneMap`'s usable input range is about one decade.** The pedestal sits at raw 0.118 (with
intensity 1.31) and `whitePoint` 1.0 maps to displayed 1.0, so the window is **8.5:1**. The
sky exceeds that *within a single sun elevation*: measured from the baked gradient at 25° sun,
the horizon's blue channel is 0.4875 and the zenith's red is 0.0435 — **11:1**. Crushing is
per channel, so the zenith loses red and green while keeping blue, and where blue crosses too
the sky goes fully black. A cubemap stores every direction including the anti-solar sky, which
is darker still, so a large fraction of it is under the pedestal.

The gradient variants escape this because the hand-authored one is inverse-tone-mapped into
the usable band by construction, and the baked one samples **sunward only**, which is the
bright end.

**Parked, deliberately.** Fixing it means one of:
- making the cubemap display-referred (tone mapped at bake time, shader skips `toneMap` for
  that path) — most faithful to what a skybox actually is, a painted image of finished
  colours, but it breaks the shared output pipeline that makes the variants comparable;
- retuning the shared tone map — which would invalidate every measurement taken so far;
- a gain, which cannot work: the range needing compression is 100:1 against a 8.5:1 window.

All three are decisions with thesis consequences and belong with the RQ1 figure work, not with
a bug fix. `baseline-cubemap`'s **cost** is already measured and is identical to the gradient's
to within 0.003 ms, so nothing on the critical path is blocked.

> The wider point is an RQ3 observation about the inherited implementation: a tone map with a
> one-decade usable window cannot display a physically based sky's true dynamic range, and
> that constrains every sky variant equally.

---

## Hazard: baked assets go stale silently

Three assets are derived, and **nothing detects when their inputs have moved on**. There is no
error, no warning, and no visual cue — just a baseline that quietly no longer corresponds to
the renderer it was derived from. Given that the whole point of `baseline-baked` is to be *the
physically based sky, flattened*, a stale bake makes the comparison meaningless while looking
entirely healthy.

| asset | silently invalidated by |
|---|---|
| `SkyGradient.exr` (hand-authored) | any change to `BaselineSkyRenderer`'s `intensity`, `contrast` or `whitePoint` — it is inverse-tone-mapped against them |
| `SkyGradientBaked.exr` | any `AtmosphereEffect` scattering parameter, the transmittance LUT, `BakeAltitude`, `BakeAzimuthDegrees`, `ScatteringSteps` |
| `SkyCubemap.asset` | all of the above plus `CubemapSunElevationDegrees` |

This already bit once: the tone-map constants were corrected (intensity 1 → 1.31, whitePoint
1.1 → 1) and **both** gradients silently became wrong until regenerated — the hand-authored one
because its inverse mapping no longer matched, the baked one because the pedestal moved
underneath it.

**Proposed fix, for whenever the bakes are next touched:** a *bake stamp*, in the same spirit
as `plan_hash` and `pose_hash`. Hash the inputs at bake time (the atmosphere's shader values,
the bake parameters, the tone-map constants) and store it beside the asset. At run start,
recompute from the live scene and compare; on mismatch, warn and record `BAKE_STALE` in
`run.json`'s warnings. That converts an invisible failure into a loud one, which is the same
move `COUNTERS_UNAVAILABLE` and `CAPTURE_RUN_NOT_MEASURED` make.

Until that exists, the protocol is manual: **re-run both bakes after touching the atmosphere
or the tone map, before any measured run.**

---

## Sequencing: measurements and the report come at the end

Real measurement runs and report writing happen once the **whole** system is assembled,
clouds included — not at the end of each milestone.

The renderers are still changing, and numbers taken mid-build get invalidated by the next fix.
That already happened twice over: the fog-over-sky bug and the mismatched tone-map constants
each superseded a full release batch.

So the benchmark runs done so far were **harness validation and bug-hunting**, not results.
They earned their keep that way — they caught the black `noatmo` sky, the mislabelled
decomposition, the fog painting over the sky, the stripped-counter false pass, and the
tone-map pedestal — but none of their numbers should be quoted.

Findings go in this file for later rather than being acted on as if final.

---

## Atmosphere: aligning with Hillaire 2020

### The "hardcoded to 1" is the Rayleigh *phase*, and why removing it looks broken
`AtmosphereCommon.hlsl:158` sets `rayleighPhaseValue = 1`, with the correct call commented out
on 157. A normalised phase averages 1/4π over the sphere, so this **inflates Rayleigh
in-scatter by 4π ≈ 12.57×**. Re-enable it and the sky drops by that factor — which is exactly
why it appears to be load-bearing.

It cannot be compensated in σ. In-scatter is *linear* in σ_s but transmittance is *exponential*
in σ_t, so scaling σ by 4π takes blue vertical optical depth from 0.75 to 9.4 and the planet
disappears behind fog. **σ is structurally excluded.** The missing term is solar illuminance,
which the implementation does not have at all — absolute scale is absorbed by a display-side
`intensity` and a free `wavelengthScale`.

Corroborating: Mie *does* get its correct phase, so Rayleigh is over-weighted relative to Mie
by 12.57×, and `mieCoefficient` was raised to 0.38 to compete.

### Measured facts, verified rather than assumed
| quantity | value | reference |
|---|---|---|
| vertical optical depth (R,G,B) | 0.173, 0.420, 0.748 | Earth 0.046, 0.109, 0.265 — **2.8–3.9× too thick** |
| `getSunTransmittance` sampling bias | optical depth **13.9% low** | right-Riemann; midpoint would be 0.5% |
| minimum extinction over the column | **1.86e-05** | not 0.02 |
| `max(1e-4, σ)` clamp fires from | **h01 = 0.856**, top 14% | red first, blue from 0.95 |
| ozone positivity headroom | breaks at `ozoneStrength ≥ 0.69` | currently 0.4 — only 1.7× |
| tone map usable band | [0.1185, 0.6449], factor **5.44** | at `whitePoint = 1` Reinhard is an exact identity |

### Correction: stage 0f was not a strict no-op
I committed the stable-integral change describing the clamp as inactive at shipped values,
on the basis that minimum extinction was ~0.02. **It is 1.86e-05.** The clamp was already
firing across the top 14% of the atmosphere and understating in-scatter there by up to 5×.

The change is therefore a real behaviour change, confined to a region whose density is ~1e-5
of sea level — which is why the sky looked identical. The claim should have been "no visible
change", not "provably a no-op", and the distinction matters because the whole point of stage
0 was that its steps were verifiable as inert.

Two other numbers from the design analysis also failed checking and are corrected above: the
minimum extinction, and the ozone headroom (0.69, not 1.15).

### Validation harness
`Testbed → Atmosphere → Validate` — a menu item rather than NUnit, matching the existing stats
self-test, because a test asmdef cannot reference the predefined `Assembly-CSharp`.

The split matters: `AtmosphereReference.cs` is a **C# mirror** of the shader's density, phase
and tone-map functions, so properties can be checked at 10,000 sample points without a GPU.
A mirror can diverge from what it mirrors, and exactly one check closes that gap — the
transmittance LUT readback compares the shader's own output against this file's quadrature.
If those two disagree, the mirror is stale and every other check is suspect.

The LUT check is deliberately compared against **two** references: the exact closed form, and
the 40-step right-Riemann sum the shader actually computes. Matching the Riemann value proves
the shader implements the model; the gap to the closed form *is* the sampling bias, quantified.

### First validation run — the harness found its own bug, and the shader passed
Every predicted value came back within rounding, and the check that matters most passed
cleanly: **the LUT readback agrees with the C# mirror to 6.3e-06**, which is what establishes
that the mirror is a faithful transcription and therefore that the other checks mean something.

Confirmed against the shipped atmosphere:

| | measured |
|---|---|
| vertical optical depth | (0.173, 0.420, 0.748) — **3.73 / 3.87 / 2.82× Earth's** |
| sampling bias | optical depth **13.83% low** |
| ozone headroom | negative extinction at `ozoneStrength ≥ 0.70`, currently 0.40 |
| tone map band | [0.1185, 0.6449], factor **5.44** |
| sun disc | **4.31×** in angle, **18.59×** in solid angle |
| phase normalisation | Rayleigh and Cornette–Shanks both integrate to 1 within 3.4e-08 |
| Cornette–Shanks at g=0 vs Rayleigh | identical to **0** |

The single FAIL was mine. `VerticalOpticalDepth` accumulated 200,000 terms of ~4e-3 into a
**float** running total that reaches ~78, so every addition truncated in the same direction and
the sum came out systematically low — 0.70812 against the closed form's 0.70853, forty times
the tolerance. Reproduced the exact figure by simulating float32 accumulation, then fixed by
accumulating in double. A useful reminder that a validation harness needs validating too: had
the tolerance been looser this would have silently passed and quietly poisoned every optical
depth number in the report.

Two display bugs the run exposed: the minimum extinction printed as `0.0000` under `F4` when
the value that matters is 1.9e-05, and the sun's reference angle rendered as `0,2667` from a
raw interpolation — the sv-SE decimal comma leaking into the report despite this project having
been bitten by that before.

### Bake stamp implemented
The staleness hazard recorded earlier now has a detector. `SkyBakeStamp` (a ScriptableObject
in `Resources`) records, per baked asset, a hash of the scene values it was derived from;
`BenchmarkEnvironment.Validate` recomputes from the live scene and emits `BAKE_STALE:<asset>`
into `run.json`'s warnings, next to the numbers the staleness invalidates.

Two design points worth keeping:

- **Only scene-derived values enter the hash.** The bakers' own constants — altitude, azimuth,
  step counts, resolutions — are recorded in readable form for diagnosis but deliberately
  excluded, because they change only by editing the baker, which is visible in a diff, whereas
  a scene parameter changes by dragging a slider and leaves no trace at all.
- **Each asset declares its recipe**, so the check knows what it depends on rather than
  inferring it. `SkyGradient.exr` is `ToneMap` — it is inverse-mapped against the tone-map
  constants and stales when those move even if no atmosphere parameter did. `SkyGradientBaked`
  and `SkyCubemap` are `Atmosphere` — they store raw radiance, so the tone map is applied at
  runtime and does *not* stale them. Getting that backwards would have made the stamp fire on
  the wrong changes and stay quiet on the right ones.

A first attempt reconstructed each entry's inputs by parsing the stored text and guessing which
parts came from where. That was clever and brittle; the recipe enum replaced it.

### Stage 1a caught two problems, both worth keeping
`Testbed → Atmosphere → Validate` reported the LUT zenith at (0.9987, 0.9967, 0.9942) against
an expected (0.8642, 0.6943, 0.5251) — optical depth **110.4–112.2× too small**, with the
blue/red ratio still 4.47 against the coefficients' 4.39.

The colour being right while the magnitude was out by exactly `atmosphereThickness` is the
signature of **old shader code running against new uniforms**: the previous march divided every
step by the thickness, and the new coefficients already are per world unit, so the division
happened twice.

> **Unity does not track `.hlsl` includes as import dependencies for `.compute` files.**
> Editing `AtmosphereCommon.hlsl` left all four computes running the previous code. Touching
> the `.compute` files forces the reimport; a note to that effect now sits in each of them,
> because the failure mode is a plausible-looking sky rather than an error.

Separately, the run produced `Property (TransmittanceLUT) at kernel index (0) is not set` —
**a real regression from stage 0b.** Compute shader texture bindings do not survive a domain
reload, and nothing restored them: the per-frame re-init that 0b removed had been silently
re-binding everything every editor frame. Removing the per-frame *dispatch* was worth doing;
removing the per-frame *rebind* was not.

Split into `BindComputeResources()`, called unconditionally on every `SetProperties` because
binds are cheap, with render-texture creation and the transmittance dispatch left behind the
dirty flag. That keeps 0b's benefit without its regression.

Worth noting what this says about the harness: neither problem was visible in the image at a
glance, and both were caught by a numeric check against a closed form within minutes of being
introduced.

### Stage 1b: geometry
Four fixes, all with visible consequences.

**Ground intersection in `raymarch`.** Nothing clipped the march at the planet, so a downward
ray integrated the full atmosphere chord *through the planet's interior* — and because altitude
is clamped at zero, those interior samples were evaluated at **sea-level density**. The planet
was not an occluder but a solid block of maximum scattering. The earth-shadow test hid most of
it by zeroing the sun term, which is why it never looked obviously wrong.

The altitude clamp stays, but is now defensive rather than load-bearing: `getSunTransmittance`
still follows Bruneton's convention of ignoring the ground and leaving occlusion to the
caller's shadow test, and without the clamp those samples would take a negative altitude into
`exp(-h/H)` and the density would explode rather than vanish.

**Midpoint sampling in `getSunTransmittance`.** It advanced before sampling — a right-Riemann
sum, so every sample landed where density was already below the interval average. Predicted
effect: quadrature error in blue **13.88% → 0.354%** at the same 40 steps, sun transmittance
dropping **9.6%** in blue. The sky should darken slightly and warm.

**Aerial perspective ray length.** The slice's far distance is measured from the camera but the
march starts at the atmosphere boundary, so the distance covered getting there has to come off.
Invisible from inside the atmosphere where the two origins coincide — but the testbed camera
reaches altitude 250, radius 400 against an atmosphere top of 259.5, so it spends real time
outside, where the far endpoint over-extended by up to a whole atmosphere thickness.

**`bodyRadius` 149.5 → 150**, matching `TerrainHeightSettings.worldRadius`. The atmosphere's
ground sphere sat half a unit *inside* the terrain, so every altitude was biased and the
ray-planet test could miss ground the depth buffer saw.

This is also the first real exercise of the bake stamp: changing `bodyRadius` should make all
three baked assets report `BAKE_STALE`.

### Stage 1c: Hillaire's physical constants
The unit mapping is fixed by declaring the atmosphere column to be Earth's 100 km:
**1 world unit = 0.90909 km**. Chosen on the column rather than the planet because the terrain
globe is pinned at radius 150, so the planet cannot also be Earth-sized. The honest trade:
every scale height, ozone altitude and optical depth becomes directly comparable to a
published value, while **curvature stays wrong** — thickness/radius 0.733 against Earth's
0.0157.

A pleasing traceability result fell out. Hillaire's β_R is **exactly a λ⁻⁴ family through
(680, 550, 440) nm** — the implied `wavelengthScale` agrees to 0.002% across all three
channels — so his constants drop straight into the existing `(scale/λ)⁴` machinery, which is
also what the report's "σ ∝ 1/λ⁴" statement describes. No new plumbing, and the numbers are
citable rather than fitted.

| | before | after | source |
|---|---|---|---|
| λ | (639.5, 526, 441.8) | (680, 550, 440) nm | Hillaire |
| wavelengthScale | 748.5 | 593.483 | derived, not fitted |
| Rayleigh scale height | 9.46 u = 8.6 km | 8.80 u = **8.0 km** | report §2.1 |
| Mie scale height | 8.80 u = 8.0 km | 1.32 u = **1.2 km** | report §2.1 |
| ozone peak / half-width | 13.2 / 29.7 u | 27.5 / 16.5 u = **25 / 15 km** | report §2.1 |
| ozone red absorption | **−3** (adds energy) | +0.65 | Hillaire |
| scale height ratio | 1.075 : 1 | **6.67 : 1** | physical |

The scale heights and the ozone tent were **already committed on paper** in report §2.1, so
adopting Hillaire's coefficients is not an invention — it is the only coefficient set
consistent with the density profiles the report has already published.

Predicted: vertical optical depth (0.173, 0.420, 0.748) → **(0.0662, 0.1468, 0.2761)**, and
LUT zenith (0.8416, 0.6577, 0.4746) → **(0.9359, 0.8635, 0.7587)**. Much more transparent.

**Expected sampling problem.** With H_Mie at 1.32 units the 256-step uniform march is
marginal and in places inadequate:

| view | units per step | samples per Mie scale height |
|---|---|---|
| zenith from ground | 0.43 | 3.1 |
| horizon from ground | 0.83 | 1.6 |
| through, from altitude 220 | 1.66 | **0.8** |

So high-altitude and horizon views may band or shimmer. That is 1d — non-uniform stepping —
and the requirement is itself an RQ3 finding: *physical scale heights force non-uniform
sampling*. Left separate deliberately so the visual change from the constants can be judged on
its own before the sampling changes underneath it.

---

## Why the physical constants cost the sunsets — a geometry result, not a tuning one

Stage 1c landed exactly as predicted (optical depth now 1.04–1.43× Earth's, down from
2.8–3.9×; LUT zenith 0.9376/0.8653/0.7607). The sky is bluer and sunrises and sunsets are much
less warm until the sun is nearly on the horizon. Two separate causes, and the second is the
important one.

**The bluer cast is the negative ozone leaving.** Red's vertical ozone optical depth was
**−0.0274** — a *negative* optical depth is a gain, so the ozone term was amplifying red. With
it at a physical +0.0097, manufactured warmth disappears and the balance shifts blue.

**The weak sunsets are the planet being too small, and no coefficient can fix it.** A red
sunset requires blue to be extinguished along a long slant path. The amplification of the
horizon path over the zenith path (Chapman, 90°) is `sqrt(pi*R/2H)`:

| | radius | horizon air mass | blue optical depth at horizon | blue transmitted |
|---|---|---|---|---|
| Earth | 6371 km | **35.4×** | 9.37 | 0.00009 — blue is gone |
| this testbed | 136 km | **5.2×** | 1.43 | 0.24 — blue still dominant |

Slant paths are **6.8× shorter** than Earth's. At the horizon this atmosphere still transmits
a quarter of its blue, so the sun cannot redden until it is geometrically very low.

### This reframes the inherited implementation
The two deviations that looked most like carelessness were **compensations for exactly this**:

- coefficients ~3× Earth's — pushing zenith optical depth up so the short slant path still
  extinguishes something
- `ozoneAbsorption.x = -3` — manufacturing the red the geometry cannot produce

Lague's tuning was internally coherent as an artistic response to a planet that cannot support
Earth-calibrated constants. That is a much more interesting finding than "the constants were
wrong", and it is RQ3-shaped: *what has to change when a physically based atmosphere model
meets a world that is not Earth-sized.*

### The trade, stated plainly
On a small planet you can match Earth's **zenith** optical depth or its **horizon** optical
depth, not both. Matching the horizon needs zenith optical depth ~1.80, i.e. **6.5×** Hillaire's
— close to what the inherited implementation was doing. Matching the zenith is what the
physical constants do, and it costs the sunset.

Options, none taken yet:
1. Keep physical constants, report the limitation. Current state.
2. Earth-proportion the geometry — needs `atmosphereThickness` ~2.4 world units, which puts
   every benchmark camera altitude (4–220 units, i.e. 170–9300 km under that mapping) in deep
   space. Rejected earlier for exactly this reason.
3. Calibrate coefficients to match Earth's horizon rather than its zenith, stated as a
   deliberate adaptation with the trade recorded.

**Deferred until after stages 2 and 3.** The Rayleigh phase is precisely what creates the
angular structure of a sunset, and multiple scattering is what fills the twilight band — judging
sunset appearance before either has landed would be premature.

### To explore: a hybrid — grow the planet *and* adjust the parameters
Rather than choosing between Earth-proportioned geometry (which puts every benchmark camera in
space) and toy geometry with physical constants (which cannot make a sunset), meet in the
middle: grow the planet part of the way and cover the remaining deficit with coefficients,
stating both.

**Two knobs, not one.** The terrain globe is pinned at radius 150 world units, so the planet's
real size is set entirely by `k`, the kilometres per world unit. The second knob is
`atmosphereThickness` in world units, which should be `100/k` to keep the column at Earth's
100 km — the current setup gets this wrong by leaving the column 110 units thick regardless.

| k (km/unit) | planet (km) | thickness (u) | H/R | horizon air mass | coefficients × Hillaire | altitude 12 u |
|---|---|---|---|---|---|---|
| 0.909 (now) | 136 | 110 | 0.733 | 5.2 | 6.8 | 11 km |
| 2 | 300 | 50 | 0.333 | 7.7 | 4.6 | 24 km |
| 3 | 450 | 33 | 0.222 | 9.4 | 3.8 | 36 km |
| **5** | **750** | **20** | **0.133** | **12.1** | **2.9** | **60 km** |
| 8 | 1200 | 12.5 | 0.083 | 15.3 | 2.3 | 96 km |
| 12 | 1800 | 8.3 | 0.056 | 18.8 | 1.9 | 144 km — camera leaves the column |
| 42.5 | 6375 | 2.4 | **0.0157** | 35.4 | **1.0** | 510 km — deep space |

Air mass grows only as `sqrt(k)`, so the coefficient inflation needed to reach Earth's horizon
optical depth falls as `1/sqrt(k)` — halving the fudge costs a 4× larger planet.

**The binding constraint is the camera**, not the physics: at 12 world units it has to stay
inside the column, which caps `k` near 8. Somewhere around **k = 3–5** looks like the sweet
spot — an air mass of 9–12 against Earth's 35, coefficients only ~3–4× physical instead of
6.8×, and the strategy view at a plausible 36–60 km.

Worth noting what this would make the deviation list say: instead of "the coefficients are 3×
too large", it becomes "the coefficients are 3× physical **because** the planet is 8× too
small, and here is the curve relating the two". That is a far better RQ3 answer, and it turns
the inherited implementation's fudge into a measured adaptation.

Things to check before committing to it: terrain LOD thresholds and `TestbedCamera` altitude
limits are tuned in world units and would need revisiting; the aerial perspective's
`terrestrialClipDst` is derived from `bodyRadius`; and every camera bookmark and benchmark
view is expressed in world-unit altitudes, so their *meaning* in kilometres changes even
though the numbers do not.

---

## The 750 km planet: geometry first, density for the remainder

The sunset finding above forced a choice, and this is it. `atmosphereThickness` **110 -> 20**
world units, `heightMultiplier` **3 -> 1.76**, and one new named parameter,
`densityMultiplier = 2.9159`.

### Why the thickness is the lever
The planet's size *in kilometres* is set entirely by the km-per-world-unit mapping, and that is
fixed by declaring the column to be Earth's 100 km. Shrinking the column in world units grows
the planet in kilometres while **nothing in world units moves** - terrain, LOD, the baked data,
picking, labels and the ocean mesh are all untouched. Changing `bodyRadius` would have touched
every one of them for the same effect.

|  | before | after | Earth |
|---|---|---|---|
| km per world unit | 0.91 | **5.00** | - |
| planet radius | 136 km | **750 km** | 6371 km |
| Rayleigh scale height | 8.8 u | 1.6 u | - |
| horizon air mass | 5.2 | **12.1** | 35.4 |
| blue transmitted at horizon | 0.254 | **0.040** | 0.00009 |
| coefficient fudge needed | 6.8x | **2.9x** | 1.0x |

### The binding constraint was terrain, not the camera
Mountains were 3 world units and the scale height is `0.08 x thickness`. Once the scale height
falls below the peaks, mountains poke out of the atmosphere - which caps the thickness at ~37
(a 400 km planet) unless the terrain shrinks too. Setting `heightMultiplier` to 1.76 makes peaks
**1.10 scale heights**, exactly Earth's ratio, and unlocks the 750 km planet.

### The remainder is one named number
`densityMultiplier` scales every scattering and absorption coefficient together - i.e. this air
is 2.92x denser than Earth's at the same composition and the same vertical structure. It is set
so the **horizon** optical depth in blue matches Earth's, since that is the quantity a sunset is
actually made of, and the validation harness now reports both plus the ratio.

This is much better to defend than the alternative. The published constants stay published and
visible; there is exactly one deviation, it has a name, a value, and a stated cause (a small
planet has short slant paths), and it is *calibrated against a physical target* rather than
dialled until it looked right.

**And it lands almost exactly where Lague was.** Zenith blue optical depth is now **0.772**
against his **0.709**. The inherited magic number was approximately correct for the geometry all
along - what was missing was the reason, which is why it also needed a negative red ozone term
to finish the job. That is the RQ3 finding in one sentence.

### Consequences handled
- Camera altitude and reference altitude 10 -> 2, bookmark 40 -> 7.3. **The camera has to come
  down** or it sits above the air entirely: at the old altitude of 12 units it would be 7.5
  scale heights up and the aerial perspective would vanish.
- Benchmark pose altitudes scaled by 20/110 so each benchmark keeps its intent: altitude sweep
  4->0.73 and 220->40, daycycle 12->2.18, framing 20->3.64, orbit 30->5.45, smoke 25->4.55.
  These are guesses at the *same shot* under the new scale, not re-picked shots.
- All baked baseline assets are stale; the stamp now hashes `densityMultiplier` too.
- Tone mapping will likely need retuning: vertical optical depth nearly triples, so the sky is
  deeper and more saturated.

### Still open
Horizon air mass is 12.1 against Earth's 35.4, so this is not Earth and the density multiplier
is carrying a factor of 2.9. Going further means shorter mountains again - `heightMultiplier`
1.1 would allow a 1200 km planet at 2.3x - and a smaller visible fraction of the globe. Left
until there is a picture to judge.

### Reverted to the 136 km planet, with both scales on a key

Playing the 750 km world settled the question: it is impractical. You see too little of the
globe without zooming a long way out, and mountains at 3 world units are 1.88 Rayleigh scale
heights there, so peaks stand clear of the haze layer.

So the **136 km planet is the default again**, with the shortfall carried by `densityMultiplier
= 6.84` rather than 2.92. Both scales are now `WorldScalePreset` assets under
`Assets/Data/WorldScales`, cycled live with **F7** by `WorldScaleController`:

| preset | thickness | air mass | zenith T | horizon tau | horizon T | camera |
|---|---|---|---|---|---|---|
| `practical-136km` | 110 u | 5.2 | 0.452 | 4.11 | 0.016 | altitude 10 |
| `physical-750km` | 20 u | 12.1 | 0.452 | **9.64** | **0.00007** | altitude 4 |
| Earth | - | 35.4 | 0.767 | 9.37 | 0.00009 | - |

**Both run the same `densityMultiplier` of 3.0, so the only variable is geometry.** The sky
overhead is identical between them; the limb is what changes, and the limb is the sunset. That
is a controlled comparison rather than two separately tuned looks, and it isolates exactly what
the planet's proportions contribute.

**What a preset does NOT change is the planet.** `bodyRadius` is 150 world units in both and the
terrain is byte-identical - which is precisely why this can be swapped on a key with no mesh
rebuild. What moves is `atmosphereThickness`, so the scale height goes 8.8 -> 1.6 units and R/H
goes 17 -> 94. That ratio is the only thing horizon air mass depends on, so the physics is real,
but the kilometre labels ("136 km", "750 km") are a convention laid on top of it - 1 unit =
100/thickness km - and not a change in geometry. Making the *planet* genuinely larger, so more
terrain is visible at a flatter horizon, means `worldRadius` and a terrain regeneration. That is
a different lever from air mass and has not been touched.

An earlier version of these presets set the small one to 6.84, the value that matches Earth's
horizon exactly. It reads as fuzzy: the zenith transmits 0.16 against Earth's 0.77, and Mie is
scaled by the same factor so the forward glow smears the sun disc. That is the trade stated in
one observation - on a small planet you can match Earth's zenith or Earth's horizon, not both -
and 3.0 is the stated compromise.

Every change goes through a `RestoreScope`, disposed on preset change and on disable, because
`AtmosphereEffect` is a ScriptableObject and anything written to it in the editor reaches disk.

`heightMultiplier` is deliberately **not** in the presets: changing it means regenerating the
terrain meshes, which a keypress should not do. The consequence - mountains standing out of the
haze at the larger scale - is left visible, since it is part of what makes that scale
impractical.

The tone map is per preset. It matters less now that both share a density, since vertical
optical depth is the same in each, but the two differ in how much light the limb returns.

**For the report.** This is a concrete RQ3 answer rather than a failure: an Earth-calibrated
physically based atmosphere and a strategy-game camera want incompatible planet sizes, the trade
between them is a measurable curve (air mass grows as sqrt of radius, so the coefficient fudge
falls as 1/sqrt), and the practical resolution is to keep the playable geometry and name the
compensation instead of hiding it in six constants.
