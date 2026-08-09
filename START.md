# START.md — Starting point of the `atmos` repo

Snapshot of what exists *before* thesis work begins, so later diffs are legible and the
report can state honestly what was inherited vs. what was built.

Companion to [THESIS.md](THESIS.md), which covers the research question and theory.

---

## 1. What this repo is

A fork of **Sebastian Lague's "Geographical Adventures"** (`SebLague/Geographical-Adventures`,
MIT) — a Unity globe game where you fly a cargo plane around a real-world Earth built
from geographic data and drop packages on countries.

It was chosen because it already ships a **working, LUT-based physically based
atmosphere** on a real globe. That is the thesis's PBR-atmosphere starting point; the
game around it is scaffolding to be stripped.

**Git state at start:**
- Branch `main`, at `31efe80 Update Unity version`. Other branches: `old-changes`
  (local + remote), `origin/main`.
- History is entirely upstream Lague + community localization PRs. **No thesis commits
  yet.**
- Working tree clean except untracked `CLAUDE.md`.
- `.gitignore` is the standard Unity one; note it ignores `*.csproj`/`*.slnx` but those
  files are present and tracked-looking in the working dir — they're generated, ignore them.

**Environment:**

| | |
|---|---|
| Unity | **6000.3.21f1** (`ProjectSettings/ProjectVersion.txt`) |
| Render pipeline | **Built-in.** `m_CustomRenderPipeline: {fileID: 0}` — no URP/HDRP. Everything is `OnRenderImage`, `CommandBuffer`, and `Camera.onPreCull`. |
| Notable packages | `com.unity.inputsystem` 1.20.0 (present, but gameplay code uses legacy `Input.GetKey`) |
| Repo size | ~1 GB, dominated by `Assets/Data` (924 MB of baked world data) |

> The root `C:\dev\thesis\CLAUDE.md` says Unity 6000.2.13f1 and quotes LUT sizes of
> 1024×512 / 128³ / 512×1024. Both are out of date — see §3 for the values actually
> configured in `Atmosphere.asset`.

---

## 2. Layout

```
Assets/
├── Post Processing/          ← THE THESIS-RELEVANT PART
│   ├── Core/                 PostProcessingManager, PostProcessingEffect
│   └── Effects/
│       ├── Atmosphere/       PBR atmosphere: 3 computes, 2 shaders, 3 hlsl
│       ├── Aerial Perspective/  cheap depth-fog approximation (baseline seed)
│       ├── Blur Test/        disabled
│       └── 3rd Party/        Bloom, FXAA
├── Scripts/
│   ├── Game/
│   │   ├── Solar System/     (9) RenderingManager, SolarSystemManager, Sun, Moon,
│   │   │                         EarthOrbit, Orbit, StarRenderer, StarData
│   │   ├── Player/           (5) Player, GameCamera, Package, Parachute, PlaneTrails
│   │   ├── Quest/            (9) delivery gameplay
│   │   ├── Navigation/       (7) globe map, compass, speedometer
│   │   ├── City Lights/      (4) + compute + 2 shaders — night-side city lights
│   │   ├── Country Highlight/(1) + compute
│   │   ├── World/            (4) country/city data loading
│   │   ├── Terrain Lookup/   (1) + compute — WorldLookup height sampling
│   │   ├── Misc/             (6) GameController, CountryData, GeoMaths, RenderSettingsController
│   │   ├── Audio/ (2), UI/ (1)
│   ├── Generation/           (~25) offline bake tools: terrain meshing, LOD, JFA,
│   │                              coastlines, normals, ocean, country index maps
│   ├── Menu/                 (17) main menu, pause, settings, credits
│   ├── Localization/         (6) ~15 languages
│   ├── Input/ (4), Types/ (4)
│   ├── Shader Common/        GeoMath, Math, Shading, SimplexNoise, Triplanar (hlsl)
│   └── Shaders/Game/         Terrain, Ocean, Waves, Moon, Star, ShadowTerrain, ...
├── Data/        924 MB — baked terrain, heights, country/city data, stars, moon, localization
├── Graphics/    55 MB — aircraft, balloon, globe map, placeholder earth
├── Scenes/      Game.unity (single scene, ~1277 GameObjects) + Generators/
├── Plugins/     Seb/ (ComputeHelper, meshing, easing), TextMesh Pro, PathCreator
└── Audio/
```

403 C# files total. One playable scene: `Assets/Scenes/Game.unity`.

---

## 3. The existing atmosphere — inherited PBR implementation

`Assets/Post Processing/Effects/Atmosphere/`. The header of `AtmosphereCommon.hlsl`
credits `https://sebh.github.io/publications/egsr2020.pdf` — i.e. **Hillaire 2020**, the
thesis's primary atmosphere reference. Traceability is already partly established.

### Architecture: three lookup textures

| LUT | Type | Configured size | Compute | Cadence |
|---|---|---|---|---|
| Transmittance | 2D, `R16G16B16A16_UNorm` | **128 × 64** | `TransmittanceDepthLUT.compute` | Once, on settings change |
| Aerial perspective (luminance + transmittance) | two 3D, `SFloat` / `UNorm` | **32³** | `AerialPerspective.compute` | Every frame |
| Sky | 2D, `R16G16B16A16_SFloat`, mipmapped | **128 × 256** | `SkyRender.compute` | Every frame |

Raymarch step counts: **256** for sky, **20** for aerial perspective, **40** (hardcoded)
for the sun-transmittance march that bakes the transmittance LUT.

### Per-frame flow

1. `Camera.onPreCull` → `AtmosphereEffect.RenderLUTs()` → dispatches `SkyRender` then
   `AerialPerspective`. Guarded by `lutUpdateRequired`, which is set in
   `RenderEffectToTarget`, i.e. **the LUTs only refresh if the post-process actually ran
   last frame.**
2. `CameraEvent.BeforeForwardOpaque` → two CommandBuffers registered by
   `RenderingManager`: "Outer Space Render" (stars + moon) and "Sky Render"
   (`DrawSky.shader`, blits the sky LUT to the camera target).
3. `PostProcessingManager.OnRenderImage` → effect chain. In `Game.unity` the chain is
   **Atmosphere → FXAA → Blur(disabled)**. `Atmosphere.shader` samples the two 3D LUTs
   by `(screenUV, depthT)`, applies `originalCol * transmittance + luminance`, tone maps,
   dithers, and lerps by `aerialPerspectiveStrength`.

### Physics implemented

- **Rayleigh:** coefficients from `(scale/λ)⁴` per RGB channel; density
  `exp(-h01 / rayleighDensityAvg)`.
- **Mie:** density `exp(-h01 / mieDensityAvg)`; separate `mieCoefficient` (scattering)
  and `mieAbsorption`.
- **Ozone:** `saturate(1 - |peakAltitude - h01| · falloff)` — structurally the same form
  as the report's profile. Absorption only, no scattering. Correct.
- **Extinction:** `mie + mieAbsorption·mieDensity + rayleigh + ozoneAbsorption·ozoneDensity`.
- **Scattering integral:** uses the analytic-per-step form
  `(inScatter - inScatter·sampleTransmittance) / extinction`, which converges at much
  lower step counts than naive accumulation. Worth keeping and citing.
- **Earth shadow:** ray-sphere test against the planet zeroes sun transmittance. Enabled
  for the sky LUT, **disabled for aerial perspective** (comment: artifacts from the low
  32³ resolution).
- **Tone mapping:** extended Reinhard with white point, preceded by intensity/contrast,
  followed by a `smoothMax` floor and blue-noise dither (triangular-remapped).

### Configured values (`Atmosphere.asset`)

```
bodyRadius              149.5      atmosphereThickness   110
wavelengthsRGB          (639.5, 526, 441.8)   wavelengthScale  748.5
rayleighDensityAvg      0.086      mieDensityAvg         0.08
mieCoefficient          0.38       mieAbsorption         0.11
ozonePeakDensityAltitude 0.12      ozoneDensityFalloff   3.7
ozoneStrength           0.4        ozoneAbsorption       (-3, 3.12, 0.02)
intensity 1.31   contrast 1.45   whitePoint 1   ditherStrength 4
aerialPerspectiveStrength 0.529    skyTransmittanceWeight 0.429
```

### Deviations from the report's theory — read before writing §2.2

These are all real and all matter for RQ1/RQ3. None are bugs in the game; they're
art-directed choices that a *thesis* has to either justify or correct.

1. **The Rayleigh phase function is disabled.** `AtmosphereCommon.hlsl:157-158` computes
   `getRayleighPhase` then overwrites it: `float rayleighPhaseValue = 1;`. Rayleigh
   in-scatter is currently isotropic. This is the single largest physical deviation.
2. **The Mie phase function is Cornette–Shanks, not Henyey-Greenstein.** `getMiePhase`
   evaluates `3/8π · (1-g²)(1+cos²θ) / ((2+g²)(1+g²-2g·cosθ)^1.5)`. The report's §2.1
   only presents HG and Schlick — either add Cornette–Shanks to the background or switch
   the code. `g` is **hardcoded to 0.8**, not exposed.
3. **Scale heights are not physical.** Densities use `h01` normalized to
   `atmosphereThickness`, with `rayleighDensityAvg = 0.086` and `mieDensityAvg = 0.08` —
   a ratio of ~1.07:1. The report specifies 8000 m vs 1200 m, a ratio of **6.67:1**.
4. **Planet geometry is not to scale.** `atmosphereThickness / bodyRadius = 110/149.5 ≈
   0.74`. Earth's is ~100 km / 6371 km ≈ **0.016**. Any physical-accuracy claim needs an
   explicit world-unit↔km mapping, and it won't be a simple one.
5. **Negative ozone absorption in red** (`ozoneAbsorption.x = -3`) — a negative
   extinction coefficient is unphysical; it *adds* energy. An artistic tint hack.
6. **No multiple scattering.** Hillaire's contribution includes a real-time
   multiple-scattering approximation and a dedicated LUT. There is no such compute shader
   here — only transmittance, sky, aerial perspective. This is the most substantive
   *addition* available, and it maps directly onto the report's unwritten argument about
   `L_i` needing to be an approximation.
7. **The sky LUT is screen-space, not a sky-view LUT.** `SkyRender.compute` parameterizes
   by the camera's four corner directions (`RaymarchCommon.hlsl`), so it's a low-res
   render of the current view, not Hillaire's camera-independent lat/long sky-view LUT.
   Cheaper to reason about; loses reprojection/reuse.
8. Minor: `getSunTransmittance` accumulates a `transmittance` variable it never returns
   (dead code alongside the `opticalDepth` path that is used).

---

## 4. What already exists toward each milestone

Against the milestone list in `CLAUDE.md`:

| # | Milestone | State |
|---|---|---|
| 1 | Strip to globe + camera scaffold | **Not started.** All gameplay intact. |
| 2 | Controls + renderer-swap UI + time controls | **Partial.** `SolarSystemManager` has `dayT/monthT/yearT` sliders, `SetTimes()`, and fast-forward — a time-of-day API already exists. Camera is plane-follow only; no free/strategy camera. No renderer toggle UI. |
| 3 | Measurement harness | **Nothing.** No timing, no capture, no camera paths, no CSV. Build entirely from scratch. |
| 4 | Baseline renderer | **Seed exists.** `AerialPerspectiveSimple.cs`/`.shader` — flat colour lerped by depth, 3 parameters (`depthMinMax`, `strength`, `atmoCol`). Currently **not in the scene's effect chain**. No skybox baseline at all. |
| 5 | PBR atmosphere | **Substantially done, with gaps** — see §3. |
| 6 | Volumetric clouds | **Nothing here.** Reference implementation lives at `C:\dev\thesis\clouds` (Lague's Worley-noise raymarcher). Nothing to port *from* inside this repo. |
| 7 | Comparison tooling / sweeps | **Nothing.** |

### Assets worth keeping
- `PostProcessingEffect` / `PostProcessingManager` — effects are `ScriptableObject`s with
  an `enabled` flag, held in an ordered array on the camera. **This is already the
  runtime renderer-swap mechanism** the thesis needs; it just needs a controller.
- `RenderingManager` already demonstrates the pattern: it watches
  `atmosphereEffect.enabled` each frame and adds/removes the sky CommandBuffer to match.
- `Seb/Compute/ComputeHelper` — RT creation (2D/3D), dispatch sizing, release. Used
  throughout.
- `WorldLookup` + `WorldHeightLookup.compute` — terrain height sampling; likely useful for
  grounding cloud layers or camera altitude.
- `SolarSystemManager` / `Sun` / `EarthOrbit` — the sun-direction and time-of-day driver
  every atmosphere parameter hangs off.
- `SimpleLodSystem` + baked terrain in `Assets/Data` — the globe and its LOD, i.e. the
  scene under test.

### Candidates for removal (per `CLAUDE.md`: "systems tied to actual gameplay")
Quest system (9 files) and its UI, Package/Parachute/PlaneTrails, HotAirBalloon,
score/credits/menus (17 files in `Menu/`), Localization (6 files + data), Audio,
Compass/Speedometer. Note the plane is entangled: `GameCamera` depends on `Player` for
`Height`, `GravityUp`, `SpeedT`, `IsBoosting`, `totalTurnAngle`, and
`SolarSystemManager.CalculatePlayerDayT()` reads `player.position`. Replacing the camera
means breaking that coupling deliberately, not just deleting `Player`.

---

## 5. Known risks from this starting point

1. **Built-in RP + `OnRenderImage`.** Fine, but it means no URP volume system, no
   `ScriptableRenderPass`, and GPU timing has to be done via `CommandBuffer` /
   `Recorder` / `GraphicsFence` rather than any pipeline-provided profiler hook. Decide
   the timing method before milestone 3, since RQ2 rests on it.
2. **LUT refresh is coupled to the post-process running.** `lutUpdateRequired` is set
   inside `RenderEffectToTarget`. Disable the Atmosphere effect and the LUT dispatches
   stop too — convenient for A/B switching, but it means "atmosphere off" is not a clean
   zero-cost baseline unless verified.
3. **The 924 MB `Assets/Data` is baked output.** The `Generation/` tools regenerate it,
   but re-baking is slow. Avoid touching terrain generation unless the thesis needs it.
4. **Single scene, 1277 GameObjects.** Stripping will be a long sequence of scene edits
   with poor diffability. `CLAUDE.md`'s "confirm it still builds and runs after each
   removal" is load-bearing advice here — commit after each system removed.
5. **Legacy input** (`Input.GetKey` via `KeyBindings`) despite the new Input System being
   installed. Pick one before writing the new camera; don't straddle.
6. **`old-changes` branch exists** and hasn't been inspected. Check whether it holds
   anything relevant before assuming `main` is the whole story.

---

## 6. Immediate next steps

1. `git checkout -b thesis` (or similar) — get off upstream `main` before the first edit,
   so the inherited/authored boundary stays a clean diff.
2. Open `Game.unity`, confirm it runs on 6000.3.21f1, and **capture reference screenshots
   of the current atmosphere at several times of day** before changing anything. This is
   the only chance to record the true starting point.
3. Fix the root `CLAUDE.md`'s stale Unity version and LUT sizes.
4. Start `NOTES.md` (per the working conventions) and log the §3 deviation list as the
   first entry — it's the seed of RQ3's answer.
