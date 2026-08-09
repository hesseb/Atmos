# THESIS.md — Knowledge base for the thesis

Distilled from the LaTeX pre-study report at `C:\dev\thesis\thesis-report` (sibling
repo, separate git history — last commits are "Updates from Overleaf", so the report
is authored in Overleaf and synced down).

This file is the *what and why*. For the state of the code you're building on, see
[START.md](START.md).

---

## 1. Identity

| | |
|---|---|
| **Title** | Physically Based Cloud and Atmosphere Rendering in Strategy Games |
| **Type** | KTH Master's thesis (`kththesis.cls`, digital/print output toggle) |
| **Industry context** | Paradox Interactive — interest stated explicitly in the introduction |
| **Build** | `cd thesis-report && latexmk` (latexmk + biber, `latexmkrc` present) |
| **Entry point** | `0 - main.tex` → numbered section files |

---

## 2. The research question

> Is the visual fidelity derived from making use of physically based cloud and
> atmosphere rendering techniques in strategy games worth the additional performance
> cost they entail? If so, what special considerations can and need to be taken when
> implementing these techniques on this kind of games?

Split into three sub-questions, each with its own stated objective and challenge:

**RQ1 — Accuracy & visual fidelity.** How does the physically based method differ from
a simpler method? Objective: a visual comparison *plus* a comparison against
**meteorological classifications** (i.e. can you point at the render and name the cloud
genus?). Stated challenges: designing the qualitative comparison, and choosing which
meteorological markers to score against.

**RQ2 — Performance.** Difference in rendering time (ms) between the PBR and the
simpler textured method. Stated challenge: holding test conditions constant —
crucially the same hardware — or the numbers are invalid.

**RQ3 — Adaptation.** How can the PBR method be adapted to work practically and
performantly under strategy-game conditions? Explicitly to be answered *during
implementation*: the optimizations you make are themselves the result. **Keep a log of
every optimization and why it was needed** — that log is RQ3's answer.

### Goals (as listed in the report)
- Render a scene using physically based **atmosphere** rendering techniques
- Render a scene using physically based **cloud** rendering techniques
- Compare against cheaper alternatives on **performance, accuracy, and authoring potential**

### Methodology
Build a scene that acts as a strategy-game-like framework, implement PBR atmosphere +
clouds on it, try optimizations. Then implement the *same scene* with a simplistic
skybox/textured solution and compare on four axes:

| Axis | How it's measured |
|---|---|
| Performance | Rendering time, possibly memory usage |
| Meteorological accuracy | Identify key markers per phenomenon, score representation |
| Visual fidelity | Simple visual comparison |
| Authoring potential | Analysis of flexibility and how efficiently each can be worked with |

The bibliography contains `2afc-wiki` (two-alternative forced choice) — a psychophysics
method for perceptual comparison. It is cited nowhere in the current text, but it is
the obvious candidate if the visual comparison is ever formalized into a user study.

### Scope — explicitly *out*
- Any in-depth game-design analysis or suggestions for how the techniques would be used
- Simulating real weather systems or fluid mechanics
- Rendering the sun, stars and moon themselves in any depth (but *do* use realistic
  luminance/illuminance values for them)

### Scope — a caveat the report flags itself
Many strategy games use **flat maps**; this project runs on a **globe**. The report
acknowledges results may not transfer directly and would need porting to a flat-map
model. Worth restating in Discussion.

---

## 3. Theory the implementation must be traceable to

Chapter 2 (Background) is the substantial, written part of the report. Chapter 2's
sections 2.2 ("Sky in Computer Graphics") and 2.3 ("Strategy Games") are **headings
only** — the CG-technique half of the background is not written yet. Sections 3–5
(Method/Results/Discussion) are stubs.

### 3.1 Participating media

Air is a participating medium: it **absorbs** and **scatters** light. Emission is
dismissed as insignificant for sky appearance. Distinguishes **out-scattering** (light
leaving the flux) from **in-scattering** (light joining it from elsewhere).

Coefficients: absorption `σ_a`, scattering `σ_s`, extinction `σ_t = σ_a + σ_s`.

Transmittance for a non-constant coefficient (the general case, chosen deliberately
since light crosses media with different coefficients):

```
T(p, q) = exp( -∫₀¹ σ_t( p - t(p - q) ) dt )
```

The full radiative transfer equation the report derives step by step:

```
L(c, ω, p) = T(c, p)·L₀(p, ω) + ∫₀^‖p-c‖ T(c, c - tω) · L_in(c - tω, ω) dt

L_in(p, ω) = σ_s(p) ∫_Ω  𝑝(p, ω_i, ω) · L_i(p, ω_i) dω_i
```

**The key argument for implementers:** the report spends a full page on why the four
luminance terms `L`, `L₀`, `L_in`, `L_i` are kept distinct even though they're
conceptually the same quantity. If `L` and `L_i` were unified you'd get infinite
recursion of rays scattering into each other at every point. Therefore **`L_i` must be
an approximation** — and that approximation is exactly the multiple-scattering
approximation the implementation has to choose (Hillaire's is the intended one). The
report defers this to §2.2, which is unwritten.

Symbol table (report Table `tab:symbols-table`): `σ_t` extinction, `σ_a` absorption,
`σ_s` scattering, `c` view point, `p` point being looked at, `L` luminance,
`T` transmittance, `ω` unit direction, `𝑝` phase function, `t` parametrization variable.

### 3.2 Phase functions

A phase function gives the probability of scattering by angle `θ` between incoming and
outgoing direction. Normalized over the sphere. Depends only on `θ`, not on rotation
about the medium → media where this applies are **isotropic** (anisotropic media, e.g.
crystal/fibre structures, are out of scope).

Regime is selected by the size parameter comparing particle radius to wavelength:

```
x = 2πr / λ
```

| Regime | Condition | Character |
|---|---|---|
| **Isotropic** | — | `p(θ) = 1/4π`; the only normalized isotropic phase function |
| **Rayleigh** | `x ≪ 1` | `p(θ) = 3/16π · (1 + cos²θ)`; dual-lobe, fore+back |
| **Mie** | `x ≈ 1` | Strong forward lobe, residual back lobe |
| **Geometric optics** | `x ≫ 1` | Reflection/refraction; **out of scope** — cloud droplets fall under Mie |

**Rayleigh** is strongly wavelength-dependent — shorter wavelength scatters more, hence
blue daytime sky; at sunrise/sunset the long path has already scattered the blue away,
leaving red/orange. Dominant in clear-sky conditions.

**Mie** is essentially wavelength-*independent* — scatters all components equally, which
is why clouds are white. The true Mie phase function is expensive, so approximations:

```
Henyey-Greenstein:  p_hg(θ,g) = (1 - g²) / ( 4π (1 + g² - 2g·cosθ)^1.5 )
Schlick:            p(θ,k)    = (1 - k²) / ( 4π (1 + k·cosθ)² ),   k ≈ 1.55g - 0.55g³
```

`g` controls forward-scattering strength; `g = 0` recovers the isotropic function.
Schlick is faster; error vs HG only becomes considerable at very large `g`. The report
already frames this as an acceptable compromise — useful precedent for RQ3.

> Note: the Schlick equation in `2 - Background.tex` line 225 has a misplaced brace
> (`\frac{1-k^2}{4\pi(1+k\cos\theta}^2`) — the denominator's square renders wrong. The
> intent is unambiguous, but it needs fixing before submission.

### 3.3 Ozone

No significant scattering component — ozone **absorbs**, mostly green and red
wavelengths. Most noticeable at twilight when the sun is near the horizon: it tones
down the green/red you'd otherwise get and pushes the result blue rather than grey.

### 3.4 Atmosphere composition — the density profiles

These three expressions are the ones the implementation should match. Note they are
absolute-altitude formulas in metres:

```
Mie scattering:    d(h) = exp( -h / 1200 m )
Rayleigh:          d(h) = exp( -h / 8000 m )
Ozone absorption:  d(h) = max( 0, 1 - |h - 25000 m| / 15000 m )
```

Rationale given: Mie particles (larger, pollution- and weather-dependent) accumulate low;
Rayleigh is more evenly distributed; ozone peaks mid-atmosphere. Figure
`figures/height_density.png` plots all three.

### 3.5 Cloud taxonomy — the yardstick for RQ1

Clouds are water droplets, ice crystals and dust suspended in air; formation mechanics
are out of scope. Classification runs genera → species → varieties; **genera is the
granularity this thesis uses**.

Altitude bands: **high** > 6000 m, **mid** 2000–6000 m, **low** < 2000 m.
Basic forms: *cumulus* (heaps), *stratus* (sheet), *cirrus* (fibre/wisp). Rain clouds
take the affix *-nimbus* / *nimbo-*.

| Band | Genera |
|---|---|
| High | Cirrus (Ci), Cirrocumulus (Cc), Cirrostratus (Cs) |
| Mid | Altocumulus (Ac), Altostratus (As), Nimbostratus (Ns) |
| Low | Stratocumulus (Sc), Stratus (St), Cumulus (Cu), Cumulonimbus (Cb) |

**This table is RQ1's scoring rubric.** "Which genera can each renderer produce, and are
they recognisable at the altitude band they belong to?" is the concrete form of the
meteorological-accuracy comparison. Note the tension with the strategy-game camera: at
an aerial view you are often *above* the low band, which is exactly the kind of
consideration RQ3 asks about.

---

## 4. Sources

| Key | What it is | Role |
|---|---|---|
| `hillaire` | Hillaire 2020, *A Scalable and Production Ready Sky and Atmosphere Rendering Technique*, CGF 39(4) | **The** atmosphere reference. LUT parameterizations + real-time multiple-scattering approximation. The existing code in this repo already credits `sebh.github.io/publications/egsr2020.pdf`. |
| `hillaire-video` | SIGGRAPH PBS course talk, *Physically Based and Scalable Atmospheres in Unreal Engine* (2020) | Companion presentation |
| `noauthor_real-time_nodate` | *The Real-Time Volumetric Cloudscapes of Horizon Zero Dawn*, Guerrilla | **The** cloud reference (Schneider). Weather map, shape/detail noise, height gradients. Bib entry has no author field — fix to Schneider & Vos. |
| `buckard` | Buckard 2022, *Hybrid Rendering in 3D Map-Based Grand Strategy Games* (KTH) | Closest prior work; the strategy-game framing |
| `pbrt` | Pharr, Jakob, Humphreys, *Physically Based Rendering* 4th ed. | Theory backbone for §2.1 |
| `rtr` | Akenine-Möller, Haines, Hoffman, *Real-Time Rendering* 4th ed. | General reference |
| `lague-git` | Sebastian Lague, *Geographical Adventures* | **This repo's upstream** |
| `lague-clouds` | Sebastian Lague, *Coding Adventure: Clouds* | Reference implementation at `C:\dev\thesis\clouds` |
| `gea` | Gregory, *Game Engine Architecture* 3rd ed. | General reference |
| `2afc-wiki` | Two-alternative forced choice | Uncited; candidate for formalizing the visual comparison |
| `steen_realism_2022` | KTH thesis on procedural lichens | Uncited; likely a methodology template for evaluation |

The introduction carries a `\begin{shadedquotation} UPDATE` block with the comment
`% Update to include Schneider, Bruneton, etc` — related-work is a known gap, and
**Bruneton** is not yet in `references.bib` at all.

---

## 5. Report status — what is and isn't written

| File | Status |
|---|---|
| `1 - introduction.tex` | Written. Has `Figure TODO: IMAGE\ref{fig:victoria}` placeholder and the UPDATE related-work block. |
| `2 - Background.tex` | §2.1 "Sky in Reality" fully written (participating media, phase functions, Rayleigh/Mie/geometric, ozone, composition, cloud taxonomy). §2.2 "Sky in Computer Graphics" and §2.3 "Strategy Games" are **headings only**. |
| `3 - Methods.tex` | Stub: `\lipsum` + four empty section headings (Research Process, Experimental Design, Validity and Reliability, Data Analysis). |
| `4 - Results.tex`, `5 - Discussion.tex` | Empty stubs, commented out of `0 - main.tex` |
| `0.5 - Summary.tex` | Abstracts (English + Swedish), keywords, acknowledgments all commented out |
| `6 - Appendix.tex` | KTH colour test page only; commented out of main |

Figures present: `flux.png`, `scattering.png`, `isotropic.png`, `rayleigh.png`,
`mie.png`, `height_density.png`, `cloud_taxonomy.png`. All are §2.1 theory figures —
**there are no result figures yet**, and `fig:victoria` referenced from the intro does
not exist.

Two unwritten headings are where the implementation feeds back into the report:
§2.2 must document the technique (Hillaire LUTs, Schneider clouds) *before* Methods can
cite it, and "Validity and Reliability" in Methods is where the fixed-hardware /
fixed-camera-path constraint from RQ2 gets formalized.

---

## 6. Implications for the implementation

Things the report commits you to, that are easy to get wrong:

1. **Traceability over invention.** RQ3's answer is a list of *named, justified*
   optimizations against a *named* baseline technique. An ad-hoc approximation that
   looks good is worth nothing to the thesis unless you can say what it deviates from.
2. **Identical conditions across renderers.** Same scene, same camera path, same
   hardware, same capture pipeline. This is the *only* stated threat to RQ2's validity,
   so the measurement harness must make divergence impossible rather than merely
   unlikely — that argues for a runtime toggle, not two branches.
3. **The cloud-genera table is a requirement, not trivia.** The PBR cloud renderer needs
   authorable coverage/altitude controls capable of producing distinguishable genera, or
   RQ1 has nothing to score.
4. **Authoring flexibility is a graded axis.** Parameter count, iteration time, and how
   predictably a parameter maps to a visible outcome are all evidence — worth logging as
   you tune, since you can't reconstruct it afterwards.
5. **The three density profiles are given in metres of real altitude.** The scene's globe
   is at an arbitrary scale (`bodyRadius = 149.5`, `atmosphereThickness = 110` — see
   START.md), so any claim of physical accuracy requires an explicit stated mapping
   between world units and kilometres.
6. **Realistic luminance/illuminance for sun, moon and stars** is in scope even though
   rendering those bodies in depth is not.
