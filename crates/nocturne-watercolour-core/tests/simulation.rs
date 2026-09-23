use nocturne_watercolour_core::domain::paint;
use nocturne_watercolour_core::domain::sim::{self, PigmentCoefficients, Scratch, SimParams};
use nocturne_watercolour_core::domain::{
    BrushStroke, Operation, Palette, Paper, PaperField, Point, RadiusProfile, Seed, SimulationGrid,
    StrokeSpan, WaterStroke,
};

const N: u32 = 96;

fn fresh(seed: Seed) -> (SimulationGrid, Vec<PigmentCoefficients>) {
    let palette = Palette::moonlight();
    let field = PaperField::generate(&Paper::cold_press(seed), N, N);
    (
        SimulationGrid::new(&field, palette.len()),
        PigmentCoefficients::from_palette(&palette),
    )
}

fn disc(radius: f32, concentration: f32, water: f32) -> Operation {
    Operation::Brush(BrushStroke {
        path: vec![Point::new(0.5, 0.5)],
        radius: RadiusProfile::uniform(radius),
        pigment: 0,
        concentration,
        water,
        softness: 0.2,
        span: StrokeSpan::FULL,
    })
}

fn run(grid: &mut SimulationGrid, coef: &[PigmentCoefficients], params: &SimParams, ticks: u32) {
    let mut scratch = Scratch::for_grid(grid);
    for _ in 0..ticks {
        sim::step(grid, coef, params, &mut scratch);
    }
}

fn wet_cell_count(grid: &SimulationGrid) -> usize {
    grid.wet.iter().filter(|&&w| w > 0.0).count()
}

#[test]
fn a_drying_sheet_reaches_dry_instead_of_an_equilibrium() {
    let params = SimParams::default();
    for rate in [0.65, 3.0] {
        let (mut grid, coef) = fresh(Seed(41));
        let w = grid.width as usize;
        let (x0, x1, y0, y1) = (w / 3, 2 * w / 3, w / 3, 2 * w / 3);
        for y in y0..y1 {
            for x in x0..x1 {
                let i = y * w + x;
                grid.wet[i] = 1.0;
                grid.pressure[i] = 1.0;
                grid.saturation[i] = 0.8;
                grid.capacity[i] = 1.0;
                grid.pigments_in_water[i] = 0.5;
            }
        }
        assert!(grid.total_water() > 0.0);
        sim::apply(&mut grid, &Operation::Dry { rate }, &params, Seed(41));
        run(&mut grid, &coef, &params, 600);
        assert_eq!(
            grid.total_water(),
            0.0,
            "water must leave, not plateau (rate {rate})"
        );
        assert_eq!(
            wet_cell_count(&grid),
            0,
            "the sheet must finish drying (rate {rate})"
        );
    }
}

#[test]
fn absorption_moves_water_from_the_film_to_the_fibres() {
    let params = SimParams {
        evaporation: 0.0,
        ..Default::default()
    };
    let (mut grid, coef) = fresh(Seed(44));
    let i = grid.index(48, 48);
    grid.wet[i] = 1.0;
    grid.capacity[i] = 1.0;
    grid.pressure[i] = 0.5;
    grid.saturation[i] = 0.5;
    let film_before = grid.pressure[i];
    let fibres_before = grid.saturation[i];
    sim::pass_transfer(&mut grid, &coef, &params);
    let film_gave = film_before - grid.pressure[i];
    let fibres_got = grid.saturation[i] - fibres_before;
    assert!(
        (film_gave - fibres_got).abs() < 1e-6,
        "film gave {film_gave}, fibres gained {fibres_got}"
    );
    assert!(
        film_gave > 0.0 && film_gave <= params.capillary_absorb + 1e-5,
        "a film thicker than capillary_absorb gives only capillary_absorb, gave {film_gave}"
    );

    let (mut grid, coef) = fresh(Seed(45));
    let i = grid.index(48, 48);
    grid.wet[i] = 1.0;
    grid.capacity[i] = 1.0;
    grid.pressure[i] = 0.001;
    grid.saturation[i] = 0.5;
    let film_before = grid.pressure[i];
    let fibres_before = grid.saturation[i];
    sim::pass_transfer(&mut grid, &coef, &params);
    let film_gave = film_before - grid.pressure[i];
    let fibres_got = grid.saturation[i] - fibres_before;
    assert!(
        (film_gave - fibres_got).abs() < 1e-6,
        "a film thinner than capillary_absorb gives only what it has: gave {film_gave}, gained {fibres_got}"
    );
    assert!(
        (film_gave - film_before).abs() < 1e-6,
        "gave {film_gave} of {film_before}"
    );
}

#[test]
fn a_blooming_cell_takes_its_water_from_the_paper() {
    let params = SimParams {
        capillary_rate: 0.0,
        capillary_dry: 0.0,
        ..Default::default()
    };
    let (mut grid, _) = fresh(Seed(46));
    let i = grid.index(48, 48);
    grid.capacity[i] = 1.0;
    grid.saturation[i] = 0.7;
    grid.wet[i] = 0.0;
    let mut out_s = vec![0.0; grid.cell_count()];
    sim::pass_capillary(&mut grid, &params, &mut out_s);
    assert_eq!(grid.wet[i], 1.0, "a cell that crosses sigma must bloom");
    let gained = grid.pressure[i];
    let lost = 0.7 - out_s[i];
    assert!(
        (gained - lost).abs() < 1e-6,
        "pressure gained {gained}, saturation lost {lost}"
    );
    assert_eq!(
        gained, params.capillary_seep,
        "a cell with plenty of fibres takes the full seep"
    );
}

#[test]
fn capillary_blooms_still_fire_while_the_sheet_is_drying() {
    let params = SimParams {
        capillary_rate: 0.0,
        capillary_dry: 0.0,
        ..Default::default()
    };
    let (mut grid, _) = fresh(Seed(43));
    for c in grid.capacity.iter_mut() {
        *c = 1.0;
    }
    for s in grid.saturation.iter_mut() {
        *s = 0.61;
    }
    grid.dry_rate = 3.0;
    let saturation_before: f32 = grid.saturation.iter().sum();
    let mut out_s = vec![0.0; grid.cell_count()];
    sim::pass_capillary(&mut grid, &params, &mut out_s);
    let blooms = wet_cell_count(&grid);
    assert!(
        blooms > 0,
        "a drying sheet may still bloom: backruns are real watercolour, and a bloom now costs the fibres its water"
    );
    let water_after: f32 = grid.pressure.iter().sum::<f32>() + out_s.iter().sum::<f32>();
    let error = (saturation_before - water_after).abs();
    assert!(
        error < 0.001 * saturation_before,
        "the bloomed film came out of the fibres: water conserved to {error} of {saturation_before}"
    );
}

#[test]
fn fibres_under_a_wet_cell_dry_more_slowly_than_bare_paper() {
    let params = SimParams::default();
    let (mut grid, _) = fresh(Seed(42));
    for c in grid.capacity.iter_mut() {
        *c = 1.0;
    }
    for s in grid.saturation.iter_mut() {
        *s = 1.0;
    }
    let dry_cell = 0;
    let wet_cell = grid.cell_count() - 1;
    grid.wet[wet_cell] = 1.0;
    grid.dry_rate = 2.0;
    let mut out_s = vec![0.0; grid.cell_count()];
    sim::pass_capillary(&mut grid, &params, &mut out_s);
    assert!(
        out_s[wet_cell] > out_s[dry_cell],
        "wet cell {} should keep more water than bare paper {}",
        out_s[wet_cell],
        out_s[dry_cell]
    );
    assert!(out_s[dry_cell] < 1.0, "bare paper must give its water up");
}

#[test]
fn same_seed_is_bit_identical_and_different_seed_differs() {
    let params = SimParams::default();
    let mut runs = Vec::new();
    for seed in [Seed(11), Seed(11), Seed(12)] {
        let (mut grid, coef) = fresh(seed);
        sim::apply(&mut grid, &disc(0.2, 0.6, 1.0), &params, seed);
        run(&mut grid, &coef, &params, 120);
        runs.push(grid);
    }
    assert_eq!(runs[0], runs[1]);
    assert_ne!(runs[0].pigments_deposited, runs[2].pigments_deposited);
}

#[test]
fn stability_sweep_stays_finite_and_bounded() {
    let mut extremes = Vec::new();
    for (i, scale) in [0.0f32, 0.5, 1.0].into_iter().enumerate() {
        let p = SimParams {
            slope_gain: 3.0 * scale,
            pressure_gain: 1.5 * scale,
            viscosity: 0.25 * scale,
            drag: scale,
            pigment_diffusion: scale,
            water_diffusion: scale,
            flow_outward_eta: 0.2 * scale,
            deposition_rate: 0.5 * scale,
            lift_rate: 0.5 * scale,
            dry_deposition: 10.0 * scale,
            capillary_absorb: 0.5 * scale,
            capillary_rate: scale,
            capillary_seep: 2.0 * scale,
            capillary_sigma: 0.05 + 0.9 * (1.0 - scale),
            evaporation: if i == 0 { 0.0 } else { 0.05 * scale },
            ..Default::default()
        };
        extremes.push(p);
    }
    for params in extremes {
        let (mut grid, coef) = fresh(Seed(5));
        sim::apply(&mut grid, &disc(0.3, 4.0, 4.0), &params, Seed(5));
        sim::apply(
            &mut grid,
            &Operation::Water(WaterStroke {
                path: vec![Point::new(0.2, 0.2), Point::new(0.8, 0.9)],
                radius: RadiusProfile::uniform(0.15),
                water: 4.0,
                softness: 1.0,
                span: StrokeSpan::FULL,
            }),
            &params,
            Seed(6),
        );
        run(&mut grid, &coef, &params, 500);
        assert!(grid.is_finite(), "non-finite state for {params:?}");
        for &p in &grid.pressure {
            assert!((0.0..=sim::MAX_WATER_DEPTH).contains(&p));
        }
        for &g in &grid.pigments_in_water {
            assert!((0.0..=sim::MAX_SUSPENDED).contains(&g));
        }
        for &d in &grid.pigments_deposited {
            assert!((0.0..=1.0).contains(&d));
        }
        for (&u, &v) in grid.velocity_u.iter().zip(&grid.velocity_v) {
            assert!(u.abs() <= params.max_velocity && v.abs() <= params.max_velocity);
        }
    }
}

fn radial_profile(grid: &SimulationGrid, pigment: usize) -> Vec<(f32, f32)> {
    let n = grid.cell_count();
    let c = (N as f32 - 1.0) / 2.0;
    (0..n)
        .map(|i| {
            let x = (i % N as usize) as f32 - c;
            let y = (i / N as usize) as f32 - c;
            let r = (x * x + y * y).sqrt() / N as f32;
            (r, grid.pigments_deposited[pigment * n + i])
        })
        .collect()
}

fn mean_in(profile: &[(f32, f32)], lo: f32, hi: f32) -> f32 {
    let vals: Vec<f32> = profile
        .iter()
        .filter(|(r, _)| *r >= lo && *r < hi)
        .map(|(_, d)| *d)
        .collect();
    vals.iter().sum::<f32>() / vals.len().max(1) as f32
}

#[test]
fn wet_on_dry_darkens_the_edge() {
    let params = SimParams::default();
    let (mut grid, coef) = fresh(Seed(21));
    sim::apply(&mut grid, &disc(0.25, 0.4, 1.0), &params, Seed(21));
    run(&mut grid, &coef, &params, 600);
    sim::dry_all(&mut grid);
    let profile = radial_profile(&grid, 0);
    let centre = mean_in(&profile, 0.0, 0.1);
    let edge = mean_in(&profile, 0.16, 0.24);
    eprintln!("edge {edge} centre {centre}");
    assert!(
        edge > centre * 1.15,
        "edge {edge} should exceed centre {centre} by 15%"
    );
}

fn pigment_radius_90(grid: &SimulationGrid) -> f32 {
    let n = grid.cell_count();
    let c = (N as f32 - 1.0) / 2.0;
    let mut by_r: Vec<(f32, f32)> = (0..n)
        .map(|i| {
            let x = (i % N as usize) as f32 - c;
            let y = (i / N as usize) as f32 - c;
            let total = grid.pigments_deposited[i] + grid.pigments_in_water[i];
            ((x * x + y * y).sqrt() / N as f32, total)
        })
        .collect();
    by_r.sort_by(|a, b| a.0.total_cmp(&b.0));
    let total: f32 = by_r.iter().map(|(_, t)| t).sum();
    let mut acc = 0.0;
    for (r, t) in by_r {
        acc += t;
        if acc >= total * 0.9 {
            return r;
        }
    }
    1.0
}

#[test]
fn wet_on_wet_spreads_further_than_wet_on_dry() {
    let params = SimParams::default();
    let (mut dry, coef) = fresh(Seed(31));
    sim::apply(&mut dry, &disc(0.08, 0.6, 0.6), &params, Seed(31));
    run(&mut dry, &coef, &params, 80);

    let (mut wet, coef) = fresh(Seed(31));
    sim::apply(
        &mut wet,
        &Operation::Water(WaterStroke {
            path: vec![Point::new(0.5, 0.5)],
            radius: RadiusProfile::uniform(0.3),
            water: 1.0,
            softness: 0.3,
            span: StrokeSpan::FULL,
        }),
        &params,
        Seed(31),
    );
    sim::apply(&mut wet, &disc(0.08, 0.6, 0.6), &params, Seed(31));
    run(&mut wet, &coef, &params, 80);

    let r_dry = pigment_radius_90(&dry);
    let r_wet = pigment_radius_90(&wet);
    assert!(r_wet > r_dry * 1.3, "wet {r_wet} vs dry {r_dry}");
}

/// The centroid of suspended pigment, in normalised coordinates.
fn pigment_centroid(grid: &SimulationGrid, pigment: usize) -> (f32, f32) {
    let n = grid.cell_count();
    let (w, h) = (grid.width as usize, grid.height as usize);
    let (mut sx, mut sy, mut total) = (0.0f64, 0.0f64, 0.0f64);
    for i in 0..n {
        let g = grid.pigments_in_water[pigment * n + i] as f64;
        if g <= 0.0 {
            continue;
        }
        sx += g * ((i % w) as f64 + 0.5) / w as f64;
        sy += g * ((i / w) as f64 + 0.5) / h as f64;
        total += g;
    }
    if total <= 0.0 {
        (0.5, 0.5)
    } else {
        ((sx / total) as f32, (sy / total) as f32)
    }
}

fn sweep(from: (f32, f32), to: (f32, f32), radius: f32) -> Operation {
    Operation::Brush(BrushStroke {
        path: vec![Point::new(from.0, from.1), Point::new(to.0, to.1)],
        radius: RadiusProfile::uniform(radius),
        pigment: 0,
        concentration: 0.6,
        water: 0.9,
        softness: 0.3,
        span: StrokeSpan::FULL,
    })
}

fn without_flow(params: &SimParams) -> SimParams {
    SimParams {
        flow: paint::FlowParams {
            kick: 0.0,
            kick_max: 0.0,
            splat_out: 0.0,
        },
        ..*params
    }
}

/// A brush that was moving leaves paint that is still moving. The mark must
/// drift the way the tip went — and not so far that it leaves where it was
/// laid, which would take an artwork off its stencil.
#[test]
fn the_kick_carries_paint_the_way_the_brush_travelled() {
    let params = SimParams::default();
    let radius = 0.06;
    let stroke = sweep((0.25, 0.5), (0.75, 0.5), radius);

    let drift = |p: &SimParams| {
        let (mut grid, coef) = fresh(Seed(9));
        sim::apply(&mut grid, &stroke, p, Seed(1));
        run(&mut grid, &coef, p, 30);
        pigment_centroid(&grid, 0).0
    };
    let kicked = drift(&params);
    let still = drift(&without_flow(&params));
    let moved = kicked - still;

    assert!(
        moved > radius * 0.1,
        "the kick moved the mark {moved:.4}, less than a tenth of the brush ({radius})"
    );
    assert!(
        moved < radius,
        "the kick carried the mark {moved:.4} off where it was laid, more than the \
         brush's own radius ({radius}); an artwork would leave its stencil"
    );
}

/// A dab has no direction, only an outward push, so it must stay put.
#[test]
fn a_dab_is_pushed_outward_but_not_along() {
    let params = SimParams::default();
    let dab = disc(0.08, 0.6, 0.9);
    let cell = 1.0 / N as f32;

    let (mut grid, coef) = fresh(Seed(9));
    sim::apply(&mut grid, &dab, &params, Seed(1));
    run(&mut grid, &coef, &params, 30);
    let (cx, cy) = pigment_centroid(&grid, 0);
    let spread = wet_cell_count(&grid);

    let still = without_flow(&params);
    let (mut grid, coef) = fresh(Seed(9));
    sim::apply(&mut grid, &dab, &still, Seed(1));
    run(&mut grid, &coef, &still, 30);
    let (sx, sy) = pigment_centroid(&grid, 0);
    let unpushed = wet_cell_count(&grid);

    assert!(
        (cx - sx).abs() < cell && (cy - sy).abs() < cell,
        "a dab drifted to ({cx:.4}, {cy:.4}) from ({sx:.4}, {sy:.4})"
    );
    assert!(
        spread > unpushed,
        "the landing water did not push outward: {spread} wet cells against {unpushed}"
    );
}

/// Injected velocity must not escape the bound the advection relies on.
#[test]
fn injected_flow_stays_inside_the_speed_bound() {
    let params = SimParams::default();
    let (mut grid, coef) = fresh(Seed(9));
    // A sweep from corner to corner: the longest chord an artwork can ask
    // for, so the kick is at its cap.
    sim::apply(
        &mut grid,
        &sweep((0.02, 0.02), (0.98, 0.98), 0.1),
        &params,
        Seed(1),
    );
    run(&mut grid, &coef, &params, 30);
    for (u, v) in grid.velocity_u.iter().zip(&grid.velocity_v) {
        assert!(u.is_finite() && v.is_finite(), "velocity went non-finite");
        assert!(
            u.abs() <= params.max_velocity + 1e-4 && v.abs() <= params.max_velocity + 1e-4,
            "velocity ({u}, {v}) escaped the bound {}",
            params.max_velocity
        );
    }
}

/// Correlation between the deposit and the paper's depth (`1 - height`)
/// inside the disc's interior, away from the rim.
fn valley_correlation(grid: &SimulationGrid) -> f32 {
    let n = grid.cell_count();
    let c = (N as f32 - 1.0) / 2.0;
    let pairs: Vec<(f32, f32)> = (0..n)
        .filter(|&i| {
            let x = (i % N as usize) as f32 - c;
            let y = (i / N as usize) as f32 - c;
            (x * x + y * y).sqrt() / (N as f32) < 0.16
        })
        .map(|i| (grid.pigments_deposited[i], 1.0 - grid.paper_height[i]))
        .collect();
    let len = pairs.len() as f32;
    let (ma, mb) = pairs
        .iter()
        .fold((0.0, 0.0), |(a, b), (x, y)| (a + x / len, b + y / len));
    let (mut sab, mut saa, mut sbb) = (0.0, 0.0, 0.0);
    for (x, y) in &pairs {
        sab += (x - ma) * (y - mb);
        saa += (x - ma) * (x - ma);
        sbb += (y - mb) * (y - mb);
    }
    sab / (saa * sbb).sqrt()
}

/// Standing water runs down the tooth and settles in its valleys, so a wash
/// dries darker in the paper's low spots than the upwind flow alone leaves it.
#[test]
fn standing_water_pools_in_the_papers_valleys() {
    let dried = |params: &SimParams| {
        let (mut grid, coef) = fresh(Seed(21));
        sim::apply(&mut grid, &disc(0.25, 0.4, 1.0), params, Seed(21));
        run(&mut grid, &coef, params, 600);
        sim::dry_all(&mut grid);
        valley_correlation(&grid)
    };
    let params = SimParams::default();
    let pooled = dried(&params);
    let level = dried(&SimParams {
        pool_rate: 0.0,
        ..params
    });
    assert!(
        pooled > level + 0.05,
        "valleys should gather the deposit: {pooled} with the free-surface flux, {level} without"
    );
}

/// The free-surface flux moves water between cells and never makes or loses
/// any while nothing clamps.
#[test]
fn the_free_surface_flux_conserves_water() {
    let params = SimParams::default();
    let (mut grid, _) = fresh(Seed(5));
    sim::apply(&mut grid, &disc(0.25, 0.4, 1.0), &params, Seed(5));
    let blurred = vec![1.0; grid.cell_count()];
    let mut g = vec![0.0; grid.cell_count() * grid.pigment_count];
    let mut p = vec![0.0; grid.cell_count()];
    sim::pass_pool(&grid, &params, &blurred, &mut g, &mut p);
    let before: f64 = grid.pressure.iter().map(|&x| x as f64).sum();
    let after: f64 = p.iter().map(|&x| x as f64).sum();
    assert!(
        (before - after).abs() < 1e-3 * before,
        "{before} -> {after}"
    );
    assert!(
        p.iter().zip(&grid.pressure).any(|(a, b)| a != b),
        "nothing moved"
    );
}
