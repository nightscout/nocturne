# Pigment and compositing

The optics live in `crates/nocturne-watercolour-core/src/domain/optics.rs`
(reference) and `crates/nocturne-watercolour-infra/src/gpu/shaders/render.wgsl`
(GPU, a direct port with only f32 transcendental precision as a deviation).
This page documents the model as implemented, the two output modes and their
limits, the paper model, and the numerical limits of the simulation.

## The Kubelka-Munk model as used

Every pigment carries per-RGB-channel Kubelka-Munk absorption `K` and
scattering `S`, plus three Curtis behaviour coefficients:

| Parameter | Role |
|---|---|
| `k` (`K` per RGB channel) | Absorption coefficient |
| `s` (`S` per RGB channel) | Scattering coefficient |
| `density` (`rho`) | Settling: how strongly the pigment deposits, including out of standing water, and how far moving water can keep it suspended |
| `staining_power` (`omega`) | Resistance to lifting, and how hard the pigment bites into the fibres while the film is still wet |
| `granulation` (`gamma`) | How strongly paper height modulates deposition |

`Pigment::from_reflectance(Rw, Rb)` derives `K`/`S` from a designed reflectance
over white `Rw` and over black `Rb` with Curtis's formulas:

```
a = 1/2 * (Rw + (Rb - Rw + 1) / Rb)
b = sqrt(a^2 - 1)
S = arccoth((b^2 - (a - Rw)(a - 1)) / (b(1 - Rw))) / b
K = S(a - 1)
```

Inputs are clamped to `0.002 <= Rb <= Rw - 0.002` and `Rw <= 0.98` to avoid
the singularities at `Rb = 0`, `Rw = 1` and `Rb >= Rw`. A layer of thickness 1
over white reproduces `Rw` (tested). Twelve built-in pigments ship.

Every pixel is rendered as **one mixed layer**:

```
Kx = sum(t_k * K_k),  Sx = sum(t_k * S_k)
```

where `t_k` is the pigment's *optical* thickness (below), modulated at output
resolution by paper height for granulating pigments. With
`beta = sqrt(Kx^2 + 2 Kx Sx)`:

```
R = Sx * sinh(beta)/beta / (a/b * sinh beta + cosh beta)
T = 1 / (a/b * sinh beta + cosh beta)
```

The formulation stays finite as `Sx -> 0` (the `sinh(beta)/beta` and `a/b`
special cases) and `beta` is capped at 40. Layers stack with Curtis's
two-layer formula (`composite`). Output is linear RGB; sRGB encoding is the
exporter's job.

### Optical thickness

The simulation's pigment amount is not a KM thickness. A cell's amount is
deposited plus a fixed share (`wet_pigment_visibility`) of its suspended
pigment, whatever the film's depth, so paint is visible at nearly full
strength the moment it lands instead of darkening in as it settles. The
amount maps to optical thickness through a saturating curve,

```
g = amount^optical_gamma
t = optical_max * g / (g + optical_mid)
```

The exponent above 1 keeps faint halos faint; the ceiling means a wash
approaches masstone but never black; and because the simulation caps a dried
deposit at 1, the curve is where the headroom past the swatch lives: a
typical dried light wash lands near thickness 1 (the swatch) and a full
deposit reads well past it. Presence (the luminous alpha field) goes through
the same curve; the luminous outline mask reads the raw amount.

### Surface correction

A gum-arabic film reflects part of the light at its surface and reflects
light trying to leave it back inside, which deepens heavy paint. The
reflectance of the layer over its ground is corrected with Saunderson's form
as Sudo Aquarelle uses it, `R' = (1 - k1)(1 - k2) R / (1 - k2 R)`, blended in
by `clamp(t * surface_coverage_gain, 0, 1)` so bare paper and faint halos are
untouched. For the transparent export the correction is applied as a
per-channel factor `q = R' / R` to *both* the on-white colour and the light
the coverage layer returns, so alpha rises by the light the film withholds,
the pixel still composites to the corrected colour over white, and
`rgb <= alpha` holds. In Luminous mode it scales the on-white colour.

### The wet look

While a film is present (`wet_look = clamp(depth / sheen_depth, 0, 1)`) the
colour mix reads richer, as water index-matches the particles: its
scattering falls by `wet_scatter_loss` and its absorption rises by
`wet_absorb_gain`. The ground under it darkens by `wet_darken` and a faint
sheen is added. None of this touches the coverage mix, so alpha and the
artwork's edge do not move as it dries, and a dry frame is bit-identical to
one rendered with the wet look off. The paper's capillary saturation is not
used for a lingering damp look because it does not fall as the sheet dries.

## The subtractive to-RGBA approximation

Standard "over" compositing wants one alpha, but a glaze's colour lives in
per-channel transmittance. The exported pixel is `CompositeMode::Subtractive`
(the default, `Background::Transparent`):

```
return_c = T_c^2 / (1 - R_c)       light that goes down, reflects off white, returns
alpha    = 1 - lerp(min_c(return_c), mean_c(return_c), ALPHA_SOFTNESS)
rgb_c    = max(0, (R_c + return_c) - (1 - alpha))    premultiplied
```

With `ALPHA_SOFTNESS = 0` this is exact over white for every channel. The
shipped `ALPHA_SOFTNESS = 0.6` pulls alpha toward the mean transmittance so a
medium wash stays translucent over a dark ground instead of saturating as soon
as one channel is absorbed; the cost, bounded per channel by
`ALPHA_SOFTNESS * (mean - min)` and asserted in the tests, is a slight loss of
saturation in strongly chromatic glazes over white.

**Stated limits.** Over black the transmitting channels glow brighter than the
physical `R_c` (worst channel error at thickness 0.15 asserted `< 0.3`); over
mid tones the interreflection term `1/(1 - R*B)` is not background-aware. A
dense body pigment (e.g. cerulean in the normal palette) still reads as a
fairly opaque slab over a dark ground. This is an approximation chosen so the
output reads as watercolour on light grounds and as translucent glow on dark
ones in any compositor, not a claim of physical accuracy.

## The Luminous display mode

A pale glaze over a dark ground cannot glow under Subtractive compositing (a
thin yellow returns almost no light of its own), so dark hosts use an explicit
second mode, `CompositeMode::Luminous`, selected by
`Background::TransparentOnDark`. The colour is the pigment mix's on-white
colour `W_c = R_c + return_c` at a display alpha:

```
alpha = smoothstep(LUMINOUS_ALPHA_TOE = 0.03, LUMINOUS_ALPHA_FULL = 0.2, coverage)
      * smoothstep(LUMINOUS_EDGE_LO = 0.2, LUMINOUS_EDGE_HI = 0.8, mask)
rgb_c = W_c * alpha
```

Five display decisions are folded in:

- **Outline from where the paint is, not how thick it is.** A dried deposit is
  bare or at full thickness cell by cell (a body reads `0.00 | 1.00 1.00 ...`
  across its edge), so its outline is a staircase of cells. The coverage term
  alone put the edge at the far tail of the reconstructed ramp, where a dense
  body saturates within a quarter of a cell: a one-pixel edge tracing every
  step, which over black read as blocks. The outline now comes from a separate
  `mask`. Each tap counts as inside the paint when its cell holds more than
  `LUMINOUS_MASK_THICKNESS = 0.02`, or when at least `MASK_MAJORITY = 5` of its
  8 neighbours do (that closes pinholes and fills a staircase's inner corners
  without growing a straight edge). The flags are blended with the same cubic
  weights, and the largest over pigments is `mask`. Its 0.5 contour is the
  smoothest outline the cells allow, and its ramp is set by the cubic, not by
  the thickness. Measured on a binary slanted band at eight output pixels to
  the cell, the edge climbs from a tenth to nine tenths of its alpha in about
  half a cell at any thickness; the coverage term alone did it in 2 pixels on
  a dense body. The thickness term still fades thin glazes and halos. Internal
  rims and overlaps do not touch the mask, so they never open onto the ground.

- **Alpha from the plain mix.** Coverage is computed from the *plain* pigment
  thickness (before granulation modulation). Keeping fine paper texture out of
  alpha stops thin spots letting the dark ground through as speckle.
- **Presence dilation.** A wash's deposit has genuine pinholes where paper
  tooth left cells almost bare; a per-pixel alpha would open each onto the
  ground as a dark pit. The coverage term uses the deposit's presence
  (deposited + visible suspended thickness) **soft-dilated** around each of
  the 16 cubic reconstruction taps and blended with the same weights. The
  dilation is the larger of the tap cell's own value and a fourth-power mean
  of its 3x3 neighbourhood, weighted by a Gaussian on cell distance
  (`2^(-d^2)`, normalised to sum to 1, so the weights are exact in `f32` and
  the shader needs no `exp`):

  ```
  presence(tap) = max(tap, (sum_ij w_ij x_ij^4)^(1/4))
  ```

  A bare cell ringed by `t` comes back at `0.93 t`, so pits still close; a
  uniform field comes back unchanged, so a wash's body is untouched; a mark
  thinner than a cell keeps its own value instead of being averaged away;
  and a cell beside a boundary grades with its neighbours' values instead of
  copying the largest of them, so the alpha edge follows the deposit's
  sub-cell position rather than the lattice.
- **Damped grain in colour.** Colour comes from the granulated mix damped
  toward the plain one by `LUMINOUS_GRAIN_STRENGTH = 0.38`
  (`plain + (textured - plain) * strength`), so granulation reads as gentle
  mottling inside a continuous glow rather than stone. The paper's low-frequency
  pooling octaves sit in the same height field the granulation term reads, so
  they are damped by the same factor; the pooling the simulation deposited
  (plain thickness) is untouched.
- **Colour floor and ceiling.** Colour is evaluated at no less than the
  larger of `LUMINOUS_COLOUR_FLOOR` and the pixel's presence, and at no more
  than `LUMINOUS_COLOUR_CEILING` optical thickness. A very thin glaze's
  on-white colour is nearly white, and white times a small alpha over black
  is grey; raising a boundary or pinhole to its presence gives it its
  neighbours' colour, as it already has their alpha, so the body does not
  wear a pale rim. Past the ceiling an overlap's on-white colour only
  darkens toward mud, which over a dark ground stops reading as light.
  Between the two the colour follows the deposited thickness.

`rgb <= alpha` always holds. Over white it composites to
`1 - alpha (1 - W_c)`: thin glazes agree with the subtractive result (both
near white), medium ones come out more saturated, dense dark ones washed out.
Luminous is a display choice, not physics.

**Why the body saturates.** The constants are one value, `LUMINOUS_TUNING`, so
`render_with_luminous_tuning` can render alternatives. A translucent tuning
(`alpha_full 0.45`, `grain_strength 0.7`) was re-rendered against the soft
dilation and rejected again, for a different reason than before. Its edges were
fine; its bodies were not. A pale glaze at partial alpha over near-black is
grey, so `header-motif`'s mountains and `moonlit-shoreline`'s sea clouded into
grey blotches. `alpha_full` stays at 0.2, and a mark that should read as
translucent on dark (a paint drop) is laid at a lower concentration instead.

**Resolution.** A finer grid is not the fix for a blocky outline. The fluid
moves in cells per tick, so a 512 grid paints a different picture from a 256
one (on `header-motif` the moon came out smaller and the ridges reshaped).
Every reveal runs its detail tier's own grid whatever the canvas size, so a tier
is one painting at every size, and the outline is smoothed in the render by the
mask above. At 256 over a 900 px hero a cell is 3.5 px; the mask rounds its
staircase to a gentle wobble.

**Known limitation.** With `alpha_full` at 0.2 a body saturates its alpha long
before it is dense, so on dark it carries its variation in colour alone, and
only between the colour floor and ceiling; a triple overlap still reads as a
dim brown. A preview hook
(`luminous_variants` in `render_native`) exists but is not shipped.

### When each mode is chosen

| Page background | Mode | Palette to use |
|---|---|---|
| Light | Subtractive (`Background::Transparent`) | normal palettes (`moonlight`, `water`, ...) |
| Dark | Luminous (`Background::TransparentOnDark`) | **normal palettes** (Luminous already adds presence; `for_dark_surface()` palettes would only lower alpha and drain colour toward white) |

The `<palette>_dark` variants (`Palette::for_dark_surface`, `Pigment::luminous`)
are for **subtractive compositing on dark only**: they thin and pale pigments
(`Rb x 0.3`, `Rw` pushed away from its mean and lifted toward 1, `K`, `S x 0.5`)
so a glaze can glow through transmitted hue light. Neutrals (Payne's grey, lamp
black) glow little by construction.

## The paper model

`sample_height(u, v)` in `domain::paper` sums:

- four octaves of hash-based value noise (fine grain, from `grain_scale` cells
  across the sheet, x2.3 per octave);
- two low-frequency **pooling** octaves at `0.1x` and `0.05x` the grain
  frequency, weighted `POOL_WEIGHT = 0.6` against the grain;
- a **fibre** term stretched along `x`;

all scaled by `height_amplitude` around `0.5`. Capillary capacity follows the
same height. The pooling octaves are what give a wash its broad cloudy patches:
water flows downhill into them (`slope_gain`), the stroke lays more water in
them (`STROKE_WATER_PAPER_GAIN`), and pigment is carried with the water.
Granulating pigments additionally read the full field at output resolution
(fine grain + fibre streaks) through `granulation_gain`; non-granulating
pigments stay smooth apart from the pooling they inherit from the water.

**Aspect awareness.** Distances are measured in the isotropic metric of
`scene::isotropic_scale` (shorter side `0..1`, longer `0..max(aspect, 1/aspect)`),
so grain, pooling octaves, stamps and feathers stay round on non-square scenes.
The fluid step itself is not aspect-aware (square grid by design): its per-cell
diffusion, advection and blur distances are still stretched with the grid.

## Numerical limits (`domain::sim`)

| Limit | Value | Why |
|---|---|---|
| `DT` | `1` per tick | fixed timestep; determinism |
| max velocity | `0.45` cells/tick | keeps `|u| + |v| < 1` so upwind advection never drains a cell |
| viscosity | `0.1` | explicit Laplacian weight; keep `<= 0.25` |
| pigment diffusion | `0.05` | divided by four per neighbour; keep `<= 1.0` |
| water diffusion | `0.1` | same |
| `slope_gain` | `1.6` (paper) | a still puddle's depth varies as `1.8x` the paper height (pooling) |
| `pressure_gain` | `0.9` (depth) | same |
| `drag` | `0.02` | |
| divergence relaxation | fixed **8** Jacobi iterations | not a tolerance, so CPU and GPU do identical work |
| edge drain | `eta * (1 - M_blurred) * clamp(p / DRAIN_DEPTH, DRAIN_MIN, DRAIN_MAX) * (1.5 - h)` with `eta = 0.06`, `DRAIN_DEPTH = 0.5`, clamp `[0.15, 2.0]` | pooled water at the boundary drains harder than a thin film on a high spot, varying the dried rim's weight |
| stroke water | `water * coverage * (1 + 0.5 * (0.5 - h) * 2)` | never negative |
| clamps | deposited pigment `<= 8.0` (`MAX_DEPOSITED`), suspended `<= 8.0`, water depth `<= 8.0` | every field clamped after each pass; deposited headroom above `1.0` so a drying rim keeps the pigment it concentrates |

The stability sweep test runs 500 ticks at parameter extremes and asserts
finiteness and bounds. Documented deviations from Curtis (collocated
velocities, advected water depth, depth-weighted pigment diffusion, no capillary
destination threshold, depth- and height-scaled edge drain, paper-modulated
stroke water) are listed in the `domain::sim` module doc.

Measured behaviour on the reference: a wet-on-dry disc ends with deposited
pigment heavier in the rim band than at the centre (edge darkening, asserted
`> 1.15x`); the same brush into a pre-wetted area spreads its 90 % pigment
radius `> 1.3x` further.

## Deposition and lift (`sim::pass_transfer`)

A deep film is not inert. Besides the settle rule, which makes a thinning film
deposit hard, each pigment keeps leaving the water while the cell is wet: by
`density` (heavy pigment drops out of standing water, `wet_settle`) and by
`staining_power` independent of density (a dye-like pigment adsorbs onto the
fibres, `stain_bite`). So the outward flow to a drying rim carries only what
has not already settled, and a staining wash keeps colour in its centre while a
sedimentary one gives more of it to the rim. Flow speed reduces deposition
(`carry`, weaker for denser pigment), so moving water keeps pigment in
suspension and strands it where the water slows. Lift needs water: it scales
with the wet fraction and grows with flow speed from a still-water share
(`lift_still`), still divided by `staining_power` as in Curtis.