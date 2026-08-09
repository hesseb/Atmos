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

### Done (committed)
| Commit | Stage | What |
|---|---|---|
| `149cafd` | 0 | THESIS.md, START.md, CLAUDE.md |
| `8e4cdde` | 1–2 | Decoupled `RenderSettingsController` and `LoadingManager` from `Menu/` |
| `87029c9` | 3–4 | `TestbedCamera`; boot to `Playing`; raised camera cull distance |

Two hidden compile edges pointed from keep-code into `Menu/` and had to be cut before
anything could be deleted: `struct Settings` is declared inside `Menu/SettingsMenu.cs`
but consumed by `RenderSettingsController`, and `LoadScreen` was consumed by
`LoadingManager`. Both resolved by deleting the consuming code, which turned out to be
dead — no relocation needed.

`LoadingManager.SetActiveStateAll` is now null-tolerant. **Keep that guard.**
`deactivateWhileLoading` holds `HUD`, which lives inside the `UI` root slated for
deletion, and this runs at execution order `-1100` where an NRE aborts the entire world
bootstrap behind one misleading error.

### Next: in the Unity Editor
Stages 5–8 are deletions, and scene objects must go before their scripts — a script
deleted while a scene reference survives becomes a silent missing-script component that
does not break the compile. Order per the plan file. Immediate step is installing
`TestbedCamera` on the `Camera Controller` object and removing `GameCamera` from it.

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
