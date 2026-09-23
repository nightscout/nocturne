# Provenance and licences

## Independent implementation

The engine is an **independent implementation** of the watercolour simulation
described in:

> Curtis, C. J., Banks, D. C., and Beier, T. (1997). *Computer-Generated
> Watercolor*. Proceedings of SIGGRAPH '97 (ACM SIGGRAPH Conference
> Proceedings), pp. 421-430. ACM. https://doi.org/10.1145/258734.258896

The reference simulation step (`sim::pass_*`), the Kubelka-Munk optics and the
stamp rasterisation are derived from that paper's model: the velocity/pressure/
flow/transfer/capillary passes map 1:1 onto Curtis's UpdateVelocities,
RelaxDivergence, FlowOutward + MovePigment, TransferPigment + evaporation and
SimulateCapillaryFlow. Deliberate deviations from the paper (collocated
velocities, advected water depth, depth-weighted pigment diffusion, a
depth- and height-scaled edge drain, paper-modulated stroke water, a
standing-water curl-noise swirl) are listed in the `domain::sim` module doc.

[sudaquarelle.com](https://sudaquarelle.com) was used as a **visual reference
only** during tuning: it informed judgements about what a wash should look
like. **No code and no assets were taken from it**, and it is not a dependency
of anything here.

No art assets are copied from any third party; the artwork catalogue is
procedural (a seed, a palette and a timeline define each artwork).

The Lucide SVG path grammar and flattening in `authoring/svg.rs` are an
independent implementation: the element list is parsed and flattened by this
library's own code, and no code is copied from `lucide`. `lucide` supplies only
the geometry of the icons the host chooses to render.

## Third-party marks

`github-mark` is a schematic watercolour silhouette of GitHub's logo, for
screens that name GitHub as a service the product connects to. The GitHub
logo and name are trademarks of GitHub, Inc.; nothing here is a GitHub
product and nothing is endorsed by them. It is drawn from the public
silhouette rather than traced from their artwork, and it is used only where
the service itself is being named.

If a screen ever needs it for anything else — decoration, a generic icon, a
badge implying a relationship — use a different artwork.

## Licences

The three Rust crates are **AGPL-3.0-only** (their `Cargo.toml`s say so),
`publish = false`; the `@nocturne/watercolour` package is `private: true` with
no `license` field in its manifest (see below). This section lists every
**direct** dependency and its licence.

### Rust crates

Versions are the resolved ones in `crates/Cargo.lock`; licences were read from
each crate's `Cargo.toml` in the local registry cache
(`~/.cargo/registry/src/`).

`nocturne-watercolour-core` has **no dependencies** (`[dependencies]` is empty
and enforced by `tests/boundary.rs`).

`nocturne-watercolour-infra`:

| Dependency | Version | Licence |
|---|---|---|
| `nocturne-watercolour-core` | path | AGPL-3.0-only |
| `wgpu` | 30.0.1 | MIT OR Apache-2.0 |
| `bytemuck` | 1.25.2 | Zlib OR Apache-2.0 OR MIT |
| `serde` | 1.0.228 | MIT OR Apache-2.0 |
| `serde_json` | 1.0.150 | MIT OR Apache-2.0 |
| `png` | 0.18.1 | MIT OR Apache-2.0 |
| `pollster` (native target only) | 1.0.1 | Apache-2.0/MIT |
| `web-sys` (wasm32 target only) | 0.3.105 | MIT OR Apache-2.0 |

`nocturne-watercolour-wasm`:

| Dependency | Version | Licence |
|---|---|---|
| `nocturne-watercolour-core` | path | AGPL-3.0-only |
| `nocturne-watercolour-infra` | path | AGPL-3.0-only |
| `serde` | 1.0.228 | MIT OR Apache-2.0 |
| `serde_json` | 1.0.150 | MIT OR Apache-2.0 |
| `wasm-bindgen` (wasm32) | 0.2.128 | MIT OR Apache-2.0 |
| `wasm-bindgen-futures` (wasm32) | 0.4.78 | MIT OR Apache-2.0 |
| `js-sys` (wasm32) | 0.3.105 | MIT OR Apache-2.0 |
| `web-sys` (wasm32) | 0.3.105 | MIT OR Apache-2.0 |
| `console_error_panic_hook` (wasm32) | 0.1.7 | Apache-2.0/MIT |
| `pollster` (native dev-dependency) | 1.0.1 | Apache-2.0/MIT |

The `wasm-bindgen-cli` used at build time is 0.2.128 (matching `wasm-bindgen`).

### The `@nocturne/watercolour` package

From `src/Web/packages/watercolour/package.json`: **no runtime npm
dependencies** (the `dependencies` section is absent). `svelte >= 5.0.0`,
`tailwindcss >= 4.0.0` and `lucide >= 0.400.0` (ISC) are **peer** dependencies
(the component surface is Svelte 5 runes; the host supplies a Lucide `IconNode`
for the `icon` prop). Build/dev tools are dev-only: `svelte` (5.55.5, MIT),
`@sveltejs/vite-plugin-svelte` (MIT), `vite` (6.4.1, MIT), `vitest` (4.1.6,
MIT), `svelte-check`, `typescript`, `@types/node` - none ship to consumers.

The showcase package (`@nocturne/watercolour-showcase`) additionally depends on
`@nocturne/ui` (workspace), `lucide` (ISC, ^1.47.0 - the Playground's icon
source), `@lucide/svelte` (ISC), `bits-ui`, `mode-watcher`, `svelte-sonner`,
`tailwindcss`, `@tailwindcss/vite` and the SvelteKit toolchain for its own
pages; its licences were not individually re-verified here.

All licences above were confirmed from local metadata. Nothing in this set is
unconfirmed.