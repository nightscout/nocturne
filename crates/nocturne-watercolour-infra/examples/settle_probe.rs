//! Prints what the sheet is doing tick by tick: how much water it still
//! holds, how much pigment is suspended versus deposited, and how many cells
//! are still wet. Diagnostic for a reveal whose tail stops changing before it
//! ends — the rendered frames alone cannot say whether the water left, the
//! pigment settled, or the flow simply stopped.
//!
//! ```text
//! cargo run -p nocturne-watercolour-infra --example settle_probe --release -- [id] [every] [gpu]
//! ```
//!
//! A third argument of `gpu` runs the wgpu engine instead of the CPU
//! reference and reads the grid back each sample, so the two ports can be
//! compared over the whole run rather than only at the finished frame.
//!
//! Optional `--key value` flags override the simulation parameters for that
//! run only (the defaults in `SimParams` are untouched), so a settle-gate fit
//! can sweep candidates without rebuilding the artwork:
//!
//! ```text
//! settle_probe crescent-moon 20 --wet-hi 0.06 --dry-deposition 30 --deposition-rate 0.0005
//! ```

use nocturne_watercolour_core::application::{CpuEngine, Playback};
use nocturne_watercolour_core::domain::optics::RenderParams;
use nocturne_watercolour_core::domain::sim::SimParams;
use nocturne_watercolour_core::domain::{Background, Palette, Scene, Seed, SimulationGrid};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DEFAULT_INTENSITY, DetailLevel};
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};

/// One sampled row: the sheet's water, wet cells and pigment at a tick.
fn row(tick: u32, grid: &SimulationGrid) {
    let water: f32 = grid.pressure.iter().sum();
    let wet = grid.wet.iter().filter(|&&w| w > 0.0).count();
    let suspended: f32 = grid.pigments_in_water.iter().sum();
    let deposited: f32 = grid.pigments_deposited.iter().sum();
    println!("{tick:5}  {water:7.1}  {wet:8}  {suspended:9.1}  {deposited:9.1}");
}

fn run_gpu(scene: Scene, total: u32, every: u32, params: SimParams) {
    let ctx = GpuContext::try_new()
        .expect("gpu context")
        .expect("no gpu adapter");
    let engine = GpuEngine::with_params(ctx, params, RenderParams::default()).expect("gpu engine");
    let mut pb = Playback::new(engine, scene, 3000.0).expect("playback");
    let mut tick = 0;
    loop {
        row(tick, &pb.simulator().read_grid().expect("grid"));
        if tick >= total {
            break;
        }
        let step = every.min(total - tick).max(1);
        pb.advance_ticks(step).expect("advance");
        tick += step;
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let mut positionals = Vec::new();
    let mut flags = Vec::<(String, f32)>::new();
    let mut i = 0;
    while i < args.len() {
        if let Some(key) = args[i].strip_prefix("--") {
            let val: f32 = match args.get(i + 1).and_then(|s| s.parse().ok()) {
                Some(v) => v,
                None => {
                    eprintln!("{} needs a number", args[i]);
                    std::process::exit(2);
                }
            };
            flags.push((key.to_string(), val));
            i += 2;
        } else {
            positionals.push(args[i].clone());
            i += 1;
        }
    }
    let id = positionals
        .first()
        .cloned()
        .unwrap_or_else(|| "crescent-moon".to_string());
    let every: u32 = positionals
        .get(1)
        .and_then(|s| s.parse().ok())
        .filter(|&n| n > 0)
        .unwrap_or(20);
    let gpu = positionals.get(2).map(|s| s == "gpu").unwrap_or(false);

    let mut params = SimParams::default();
    for (key, val) in &flags {
        let target = match key.as_str() {
            "deposition-rate" => &mut params.deposition_rate,
            "settle-base" => &mut params.settle_base,
            "dry-deposition" => &mut params.dry_deposition,
            "settle-curve" => &mut params.settle_curve,
            "wet-lo" => &mut params.wet_lo,
            "wet-hi" => &mut params.wet_hi,
            "lift-rate" => &mut params.lift_rate,
            "capillary-absorb" => &mut params.capillary_absorb,
            "evaporation" => &mut params.evaporation,
            "water-diffusion" => &mut params.water_diffusion,
            "pigment-diffusion" => &mut params.pigment_diffusion,
            "flow-outward-eta" => &mut params.flow_outward_eta,
            "dry-threshold" => &mut params.dry_threshold,
            "pressure-gain" => &mut params.pressure_gain,
            "slope-gain" => &mut params.slope_gain,
            _ => {
                eprintln!("unknown flag {key}");
                std::process::exit(2);
            }
        };
        *target = *val;
    }

    let palette = Palette::by_name("moonlight").expect("palette");
    let scene = ArtworkCatalogue::by_id_for(
        &id,
        Seed(42),
        &palette,
        DEFAULT_INTENSITY,
        DetailLevel::Large,
        Background::Transparent,
    )
    .unwrap_or_else(|| {
        eprintln!("unknown artwork {id}; one of {:?}", ArtworkCatalogue::IDS);
        std::process::exit(2);
    });

    let total = scene.timeline.total_ticks;
    let settle: Vec<(u32, f32)> = scene
        .timeline
        .events
        .iter()
        .filter_map(|e| match e.op {
            nocturne_watercolour_core::domain::Operation::Dry { rate } => Some((e.at_tick, rate)),
            _ => None,
        })
        .collect();
    println!("{id}: {total} ticks, dry events {settle:?}");
    if !flags.is_empty() {
        println!("params: {params:?}");
    }

    println!("tick    water   wetcells  suspended  deposited");
    if gpu {
        run_gpu(scene, total, every, params);
        return;
    }
    let mut pb = Playback::new(
        CpuEngine::new(params, RenderParams::default()),
        scene,
        3000.0,
    )
    .expect("playback");
    let mut tick = 0;
    while tick <= total {
        row(tick, pb.simulator().grid().expect("grid"));
        let step = every.min(total.saturating_sub(tick)).max(1);
        pb.advance_ticks(step).expect("advance");
        if tick == total {
            break;
        }
        tick += step;
    }
}
