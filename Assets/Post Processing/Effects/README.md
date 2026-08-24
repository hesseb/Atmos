# Effects/

Five rendering-effect folders live here: `Atmosphere/`, `Baseline Sky/`, `Clouds/`,
`Baseline Clouds/`, `Baseline Aerial Perspective/` — plus `3rd Party/` (FXAA) and
`Blur Test/` (see NOTES.md).

## Why the PBR/baseline pairs are duplicated, not shared

`Atmosphere`/`Baseline Sky`, `Clouds`/`Baseline Clouds`, and the PBR aerial perspective
(inside `AtmosphereEffect.cs`) versus `Baseline Aerial Perspective`'s
`AerialPerspectiveSimple` are each a physically-based technique next to a cheap
comparison arm. All three pairs are **structurally duplicated on purpose** — the same
post-process hookup, the same cost-mode shape, the same composite-shader plumbing —
with only the shading model itself differing between the two sides.

This is deliberate, not an oversight, and should stay that way. From
`BaselineCloudEffect.cs`:

> Structurally a copy of CloudEffect: the same post-process hookup, the same Full/Half
> cost modes, the same offscreen-then-composite shape, and the same composite shader
> code. Only the shading model differs. AerialPerspectiveSimple and DrawSkyBaseline are
> built the same way and for the same reason — hold everything but the model constant,
> or the measurement is of two implementations rather than of two techniques.

The thesis measures a physically-based technique against a cheap one. If the two sides
shared a base class or a code path beyond what's genuinely identical, any measured
difference (cost, visual) would be partly attributable to *how the code happens to be
structured* rather than to the technique itself. Do not refactor these pairs toward a
shared implementation — that would reintroduce exactly the confound the duplication
exists to avoid.

What *is* shared, deliberately, is infrastructure that carries no technique-specific
behavior — e.g. `CachedKernel.cs` under `Clouds/`, used by both the volumetric and
baseline cloud effects for kernel-index caching, extracted into its own file "so the
volumetric and the baseline share one definition instead of one owning it and the other
reaching into it."
