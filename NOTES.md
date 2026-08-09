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
`Country Data.asset` is **9 MB and was not previously loaded by `Game.unity`** — the
country UI adds it to scene load time and memory for a scene the thesis measures.

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
