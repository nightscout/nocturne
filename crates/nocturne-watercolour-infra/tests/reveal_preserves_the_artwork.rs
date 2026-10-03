//! The reveal may change how an artwork arrives; it may not change what
//! arrives. Every catalogue id is run twice to a finished frame — once as the
//! catalogue builds it (choreographed) and once with the choreography left
//! off — and the two frames must be the same picture.
//!
//! This is the guard on the failure the choreography pass was first written
//! with: laying a stroke with a narrowed tip left the stencil that carries an
//! artwork's silhouette unfilled, and `alarm-bell` revealed as a dome and
//! `report-pages` as two rounded blobs. Nothing else in the suite noticed,
//! because every other test looks at the scene's structure rather than at the
//! picture it makes.
//!
//! Two measures, because they fail differently. The **silhouette** (alpha over
//! [`ALPHA_PRESENT`]) catches a shape that changed: a lost skirt, a rounded
//! corner, a mark that never arrived. Intersection over union is used rather
//! than a per-pixel count so a smaller mark inside a larger one still fails.
//! The **body** (mean absolute difference over the whole RGBA frame) catches a
//! picture that kept its outline but lost its weight — the pale, washed-out
//! bell that the same bug also produced.
//!
//! Both tolerances are loose on purpose. Paint that arrives over sixty ticks
//! genuinely does bleed differently from paint stamped in one, and that
//! difference is the feature. They are set to pass the drawn reveal
//! comfortably and to fail an artwork that stops being itself; the numbers
//! each id actually scores are printed by `--nocapture`.

use nocturne_watercolour_core::application::playback::{
    DEFAULT_PAINT_WALL_FRACTION, DEFAULT_TICK_BUDGET,
};
use nocturne_watercolour_core::application::{CpuEngine, Playback, ProgressCurve, Renderer};
use nocturne_watercolour_core::domain::{Background, Image, Operation, Palette, Seed};
use nocturne_watercolour_infra::authoring::{ArtworkCatalogue, DEFAULT_INTENSITY, DetailLevel};

/// Alpha at or above which a pixel counts as part of the artwork at all: the
/// coverage the pacing gate measures, and the floor under the silhouette cut.
const ALPHA_PRESENT: f32 = 0.08;
/// Smallest silhouette intersection-over-union that still counts as the same
/// shape.
///
/// Looser than the body gate below, and deliberately. Intersection over union
/// is a perimeter measure: a thin shape with a long edge — a crescent, a
/// ridgeline — loses far more of its score to a one-pixel shift than a fat one
/// does, so the same visual fidelity scores 0.86 on `distant-mountains` and
/// 0.98 on `tab-underline`. This is set under the whole catalogue with room to
/// spare, to catch a silhouette that genuinely went missing; the tight gate on
/// what the artwork looks like is [`MAX_BODY_MAE`].
const MIN_SILHOUETTE_IOU: f32 = 0.82;
/// Largest mean absolute RGBA difference that still counts as the same body.
///
/// The catalogue sits between 0.004 and 0.013, so this is under twice the
/// worst. It is the gate that would have caught the failure this file exists
/// for: the tip-and-halo reveal did not only lose the bell's skirt, it laid
/// the bell in pale lavender instead of deep blue, which is a difference an
/// order of magnitude above anything here.
const MAX_BODY_MAE: f32 = 0.02;

fn finished_frame(scene: nocturne_watercolour_core::domain::Scene, edge: u32) -> Image {
    let aspect = scene.size_hint.height as f32 / scene.size_hint.width as f32;
    let mut pb = Playback::new(CpuEngine::default(), scene, 1000.0).unwrap();
    pb.finish_immediately().unwrap();
    let h = ((edge as f32 * aspect).round() as u32).max(8);
    pb.simulator().render(edge, h).unwrap()
}

/// Share of the reference frame's peak alpha at which the silhouette is cut.
///
/// Relative, not absolute, and that is the point. A fixed threshold sits in
/// the middle of a pale artwork's range — the moon's glow, the tint at the rim
/// of a wash — so a difference far too small to see flips thousands of pixels
/// at once and the score lurches. Summing the alpha difference instead has the
/// mirror fault: it scores a pale artwork almost entirely on its faintest
/// pixels, which are the ones that carry no shape. Cutting each artwork at a
/// quarter of its own peak asks the question that matters — is the shape you
/// would recognise still there — of light and heavy artworks alike.
const SILHOUETTE_CUT: f32 = 0.25;

/// Radius of the box blur the silhouette is measured through.
///
/// Whether an artwork still reads as itself is a low-frequency question, and
/// grain is the highest-frequency thing watercolour produces — paint that
/// arrives over sixty ticks lands on the paper's tooth differently from paint
/// stamped in one, which is the whole point of the reveal. Measuring the raw
/// alpha scores that difference as a shape change: the crescent moon renders
/// pixel-for-pixel like its stamped twin to the eye and still loses a fifth of
/// its raw score to speckle around the cut. Blurring first asks about the
/// shape and not about the tooth.
const SILHOUETTE_BLUR: usize = 4;

/// The alpha channel, box-blurred, so the silhouette is a shape rather than a
/// field of grain.
fn blurred_alpha(image: &Image) -> Vec<f32> {
    let (w, h) = (image.width as usize, image.height as usize);
    let alpha: Vec<f32> = image.rgba.chunks(4).map(|p| p[3]).collect();
    if w == 0 || h == 0 {
        return alpha;
    }
    let r = SILHOUETTE_BLUR as isize;
    let mut out = vec![0.0f32; w * h];
    // Separable: rows then columns, edges clamped.
    let mut rows = vec![0.0f32; w * h];
    for y in 0..h {
        for x in 0..w {
            let mut sum = 0.0;
            for d in -r..=r {
                let sx = (x as isize + d).clamp(0, w as isize - 1) as usize;
                sum += alpha[y * w + sx];
            }
            rows[y * w + x] = sum / (2 * r + 1) as f32;
        }
    }
    for y in 0..h {
        for x in 0..w {
            let mut sum = 0.0;
            for d in -r..=r {
                let sy = (y as isize + d).clamp(0, h as isize - 1) as usize;
                sum += rows[sy * w + x];
            }
            out[y * w + x] = sum / (2 * r + 1) as f32;
        }
    }
    out
}

/// Intersection over union of the two silhouettes, each cut at
/// [`SILHOUETTE_CUT`] of the reference frame's peak alpha. IoU rather than a
/// pixel count, so a mark that shrank inside the original still fails; see
/// `the_silhouette_measure_catches_a_mark_that_shrank`.
fn silhouette_iou(drawn: &Image, reference: &Image) -> f32 {
    let (a, b) = (blurred_alpha(drawn), blurred_alpha(reference));
    let peak = b.iter().copied().fold(0.0f32, f32::max);
    let cut = (peak * SILHOUETTE_CUT).max(ALPHA_PRESENT);
    let (mut inter, mut union) = (0u32, 0u32);
    for (pa, pb) in a.iter().zip(&b) {
        let (ia, ib) = (*pa >= cut, *pb >= cut);
        inter += u32::from(ia && ib);
        union += u32::from(ia || ib);
    }
    if union == 0 {
        1.0
    } else {
        inter as f32 / union as f32
    }
}

fn body_mae(a: &Image, b: &Image) -> f32 {
    let n = a.rgba.len().max(1) as f32;
    a.rgba
        .iter()
        .zip(&b.rgba)
        .map(|(x, y)| (x - y).abs())
        .sum::<f32>()
        / n
}

/// The one that matters. A 256 px edge, and the full sweep runs at `Large`
/// detail, because the bug this guards showed up as a shape change of several
/// percent and a smaller grid hides it in reconstruction.
fn assert_each_survives_its_own_reveal(ids: &[&str], detail: DetailLevel) {
    let palette = Palette::moonlight();
    let mut worst: Vec<(String, f32, f32)> = Vec::new();
    let mut failed: Vec<String> = Vec::new();
    for &id in ids {
        let drawn = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
        )
        .unwrap();
        let stamped = ArtworkCatalogue::by_id_for_unchoreographed(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
            None,
        )
        .unwrap();

        let drawn = finished_frame(drawn, 256);
        let stamped = finished_frame(stamped, 256);
        let iou = silhouette_iou(&drawn, &stamped);
        let mae = body_mae(&drawn, &stamped);
        println!("{id:<24} iou {iou:.4}  mae {mae:.4}");
        worst.push((id.to_string(), iou, mae));
        if iou < MIN_SILHOUETTE_IOU {
            failed.push(format!(
                "{id}: silhouette IoU {iou:.4} < {MIN_SILHOUETTE_IOU}"
            ));
        }
        if mae > MAX_BODY_MAE {
            failed.push(format!("{id}: body MAE {mae:.4} > {MAX_BODY_MAE}"));
        }
    }
    worst.sort_by(|a, b| a.1.total_cmp(&b.1));
    println!("worst silhouette: {:?}", worst.first().unwrap());
    assert!(
        failed.is_empty(),
        "the reveal changed these artworks. A stroke that arrives too fast is fixed by \
         giving the pen a longer path, never by narrowing the brush:\n  {}",
        failed.join("\n  ")
    );
}

/// The dark ground renders through a different compositing mode, so it gets
/// its own pass over the ids most likely to lose a silhouette there.
fn assert_each_survives_its_reveal_on_a_dark_ground(ids: &[&str]) {
    let palette = Palette::moonlight();
    let mut failed: Vec<String> = Vec::new();
    for &id in ids {
        let drawn = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            DetailLevel::Medium,
            Background::TransparentOnDark,
        )
        .unwrap();
        let stamped = ArtworkCatalogue::by_id_for_unchoreographed(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            DetailLevel::Medium,
            Background::TransparentOnDark,
            None,
        )
        .unwrap();
        let drawn = finished_frame(drawn, 192);
        let stamped = finished_frame(stamped, 192);
        let iou = silhouette_iou(&drawn, &stamped);
        println!("{id:<24} dark iou {iou:.4}");
        if iou < MIN_SILHOUETTE_IOU {
            failed.push(format!("{id}: dark-ground silhouette IoU {iou:.4}"));
        }
    }
    assert!(
        failed.is_empty(),
        "the reveal changed these artworks on a dark ground:\n  {}",
        failed.join("\n  ")
    );
}

/// The guard's own failure mode: a silhouette measure that only counted
/// pixels would pass a mark that shrank inside the original. This pins that
/// IoU does not.
#[test]
fn the_silhouette_measure_catches_a_mark_that_shrank() {
    let solid = |edge: u32, r: f32| {
        let mut rgba = vec![0.0f32; (edge * edge * 4) as usize];
        let c = edge as f32 / 2.0;
        for y in 0..edge {
            for x in 0..edge {
                let d = ((x as f32 - c).powi(2) + (y as f32 - c).powi(2)).sqrt();
                if d <= r * edge as f32 {
                    let i = ((y * edge + x) * 4) as usize;
                    rgba[i..i + 4].copy_from_slice(&[0.2, 0.2, 0.4, 1.0]);
                }
            }
        }
        Image {
            width: edge,
            height: edge,
            rgba,
        }
    };
    // 256 px, the size the catalogue is measured at, so the blur is the same
    // fraction of the mark as it is there.
    let big = solid(256, 0.40);
    let small = solid(256, 0.34);
    let iou = silhouette_iou(&big, &small);
    assert!(
        iou < MIN_SILHOUETTE_IOU,
        "a mark at 85 % of the radius must fail the silhouette gate, scored {iou:.4}"
    );
    assert_eq!(silhouette_iou(&big, &big), 1.0);
}

/// Share of the paint phase's wall clock at which the mark must still be
/// arriving, and the share of the final area it may have covered by then.
const PACE_AT: f32 = 0.25;
const MAX_AREA_AT_PACE: f32 = 0.45;
/// The share of a stage's change from one stop to the next that may be made
/// by [`PACE_AT`] of that stage's brushwork: a steady stage is about a quarter
/// done there (the worst measured is 0.29), so 0.6 passes that with room and
/// fails a stage that is front-loaded or snaps into place.
const MAX_STAGE_DONE_AT_PACE: f32 = 0.6;
/// The least mean change a stage must make to the sheet. Area alone cannot
/// show it: once the land is down the later stages paint inside the covered
/// sheet (the sun, mist, birds), and some of them lift paint.
const MIN_STAGE_CHANGE: f32 = 1e-4;

fn covered_area(image: &Image) -> f32 {
    let present = image
        .rgba
        .chunks(4)
        .filter(|p| p[3] >= ALPHA_PRESENT)
        .count();
    present as f32 / (image.rgba.len() / 4).max(1) as f32
}

/// The companion to the silhouette gate, and the reason that gate can stay
/// strict. Preserving the artwork is trivial if the reveal never moves; this
/// is what stops a pacing failure being "fixed" by leaving a wash as one fat
/// stamp — or by narrowing the brush, which is what broke the silhouette in
/// the first place.
///
/// Measured in the viewer's time, not the simulation's: a quarter of the way
/// through the paint phase's wall clock, no more than
/// [`MAX_AREA_AT_PACE`] of the finished artwork may be on the paper. A staged
/// artwork is gated by [`assert_each_stage_is_laid_in_order`] instead.
///
/// The full sweep gates at `Large`, the tier a hero renders at. `Small` draws
/// fewer marks and so paces more easily; gating there would pass artworks
/// that arrive all at once everywhere anyone actually sees them.
fn assert_each_is_still_arriving_a_quarter_of_the_way_in(ids: &[&str], detail: DetailLevel) {
    let palette = Palette::moonlight();
    let mut slow: Vec<String> = Vec::new();
    for &id in ids {
        if ArtworkCatalogue::stages(id).is_some() {
            continue;
        }
        let scene = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
        )
        .unwrap();
        let aspect = scene.size_hint.height as f32 / scene.size_hint.width as f32;
        let (w, h) = (192u32, ((192.0 * aspect).round() as u32).max(8));
        let curve = ProgressCurve::reveal_for(&scene, DEFAULT_PAINT_WALL_FRACTION);
        let ProgressCurve::Reveal { wall_split, .. } = curve else {
            unreachable!("reveal_for always returns Reveal")
        };

        let mut pb = Playback::new(CpuEngine::default(), scene, 3000.0).unwrap();
        pb.set_progress_curve(curve);
        pb.seek_progress(PACE_AT * wall_split).unwrap();
        let early = covered_area(&pb.simulator().render(w, h).unwrap());
        pb.finish_immediately().unwrap();
        let full = covered_area(&pb.simulator().render(w, h).unwrap());

        let share = if full > 0.0 { early / full } else { 0.0 };
        println!(
            "{id:<24} area at {:.0} % of the brushwork: {share:.2}",
            PACE_AT * 100.0
        );
        if share > MAX_AREA_AT_PACE {
            slow.push(format!("{id}: {share:.2} of the artwork was already there"));
        }
    }
    assert!(
        slow.is_empty(),
        "these artworks arrive all at once instead of being drawn. The fix is to give          the pen a path to walk — hatch a wide fill into overlapping sweeps with          `Frame::hatch` — never to narrow the brush, which loses the silhouette:
  {}",
        slow.join("
  ")
    );
}

/// The pace gate for a staged artwork, which
/// [`assert_each_is_still_arriving_a_quarter_of_the_way_in`] skips: a host
/// steps it one stage at a time with a linear seek, and each stage lays a whole
/// layer (the first is the sky, about half the sheet), so a cap on the share of
/// the finished area early on measures the layer, not the pacing. Instead, per
/// stage `k`: the sheet at stop `k + 1` covers no less than at stop `k` and
/// differs from it by at least [`MIN_STAGE_CHANGE`], and a quarter of the way
/// through the stage's brushwork no more than [`MAX_STAGE_DONE_AT_PACE`] of the
/// change from stop `k` to stop `k + 1` has been made. That each stop is dry and holds exactly its first `k` stages is
/// the `hub` authoring tests' job.
fn assert_each_stage_is_laid_in_order(ids: &[&str], detail: DetailLevel) {
    let palette = Palette::moonlight();
    let mut failed: Vec<String> = Vec::new();
    for &id in ids {
        let stages = ArtworkCatalogue::stages(id).expect("a staged artwork");
        let scene = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
        )
        .unwrap();
        let aspect = scene.size_hint.height as f32 / scene.size_hint.width as f32;
        let (w, h) = (192u32, ((192.0 * aspect).round() as u32).max(8));
        let window = scene.timeline.total_ticks / stages;
        let last_strokes: Vec<u32> = (0..stages)
            .map(|k| {
                scene
                    .timeline
                    .events
                    .iter()
                    .filter(|e| e.at_tick / window == k)
                    .filter(|e| {
                        matches!(
                            e.op,
                            Operation::Brush(_) | Operation::Water(_) | Operation::Lift(_)
                        )
                    })
                    .map(|e| e.at_tick)
                    .max()
                    .unwrap_or((k + 1) * window)
            })
            .collect();
        let mut pb = Playback::new(CpuEngine::default(), scene, 3000.0).unwrap();
        pb.set_progress_curve(ProgressCurve::Linear);
        let mut stop = pb.simulator().render(w, h).unwrap();
        for k in 0..stages {
            let from = k * window;
            let pace_tick =
                from + (PACE_AT * (last_strokes[k as usize] - from) as f32).round() as u32;
            pb.seek_tick(pace_tick).unwrap();
            let early = pb.simulator().render(w, h).unwrap();
            pb.seek_tick(from + window).unwrap();
            let next = pb.simulator().render(w, h).unwrap();
            let change = body_mae(&stop, &next);
            let done = if change > 0.0 {
                1.0 - body_mae(&early, &next) / change
            } else {
                1.0
            };
            let (before, after) = (covered_area(&stop), covered_area(&next));
            println!(
                "{id:<24} stage {k}: covered {before:.3} -> {after:.3}, change {change:.4}, {done:.2} done at {:.0} %",
                PACE_AT * 100.0
            );
            if after < before || change < MIN_STAGE_CHANGE {
                failed.push(format!(
                    "{id}: stage {k} adds nothing (covered {before:.3} -> {after:.3}, change {change:.4})"
                ));
            }
            if done > MAX_STAGE_DONE_AT_PACE {
                failed.push(format!(
                    "{id}: stage {k} was {done:.2} done a quarter of the way through its brushwork"
                ));
            }
            stop = next;
        }
    }
    assert!(
        failed.is_empty(),
        "{}",
        failed.join(
            "
  "
        )
    );
}

/// How much longer than its authored wall clock an artwork's brushwork may
/// take once the frame budget has had its say.
const MAX_BRUSHWORK_SLIP: f32 = 2.0;

/// Frames a 60 Hz display gives the paint phase of a default 3 s reveal.
const PAINT_FRAMES: f32 = 3000.0 * DEFAULT_PAINT_WALL_FRACTION / (1000.0 / 60.0);

/// The third leg: the reveal has to be affordable, not just correct and
/// paced.
///
/// `Playback` caps the ticks one frame may run ([`DEFAULT_TICK_BUDGET`]), so a
/// paint phase that asks for more ticks than its frames afford cannot stutter
/// — it slips, drawing at the budget's rate and finishing late. That is a
/// deliberate trade and a mild one: the brushwork then takes the same slightly
/// longer time on every machine instead of a different time on each.
///
/// What it is not is a licence for any tick count at all. An artwork that
/// needs several times its affordable ticks would draw several times slower
/// than the 600 ms it is authored for, everywhere. This bounds that at double,
/// and prints where every artwork actually sits.
#[test]
fn the_brushwork_fits_the_frames_it_is_given() {
    let palette = Palette::moonlight();
    let affordable = PAINT_FRAMES * DEFAULT_TICK_BUDGET as f32;
    let mut over: Vec<String> = Vec::new();
    for id in ArtworkCatalogue::ids() {
        for detail in [DetailLevel::Large, DetailLevel::ExtraLarge] {
            let scene = ArtworkCatalogue::by_id_for(
                id,
                Seed(11),
                &palette,
                DEFAULT_INTENSITY,
                detail,
                Background::Transparent,
            )
            .unwrap();
            let total = scene.timeline.total_ticks as f32;
            // A staged artwork plays one stage per step, each its own reveal.
            let paint_ticks = match ArtworkCatalogue::stages(id) {
                Some(stages) => total / stages as f32,
                None => {
                    let ProgressCurve::Reveal { tick_split, .. } =
                        ProgressCurve::reveal_for(&scene, DEFAULT_PAINT_WALL_FRACTION)
                    else {
                        unreachable!("reveal_for always returns Reveal")
                    };
                    tick_split * total
                }
            };
            let ratio = paint_ticks / affordable;
            println!("{id:<24} {detail:?} paint {paint_ticks:.0} ticks, {ratio:.2} of budget");
            if ratio > MAX_BRUSHWORK_SLIP {
                over.push(format!(
                    "{id} at {detail:?}: {paint_ticks:.0} ticks against {affordable:.0} affordable"
                ));
            }
        }
    }
    assert!(
        over.is_empty(),
        "the brushwork of these artworks cannot be simulated in the frames its wall clock \
         gives it, so the reveal will run slow on every machine. Lower the artwork's tick \
         count, or raise DEFAULT_TICK_BUDGET if the per-tick cost really does fit:\n  {}",
        over.join("\n  ")
    );
}

/// Share of its ticks an artwork must still be settling for, for the long
/// tail to read as the sheet setting into the page rather than as a held
/// frame.
///
/// This is about a tail that has stopped moving. The other way a tail fails —
/// still holding pigment when the timeline's `DryAll` fires, so the last frame
/// jumps — is `no_artwork_snaps_to_its_final_state`, and the two pull in
/// opposite directions: drying later leaves more to dump. Both have to hold.
const MIN_WET_SHARE: f32 = 0.70;

/// Artworks whose sheet is dry well before the end, and the tick share each
/// one reaches today.
///
/// These three are the wide landscape scenes. They lay many thin marks rather
/// than one deep body, and a thin film is finished off by the constant sinks —
/// base evaporation and capillary absorption — long before the proportional
/// settle would have taken it down. `Operation::Settle` equalises the *rate*
/// of drying across artworks; it cannot give a sheet more water than the marks
/// put there.
///
/// This list is a ratchet, not a licence. It may shrink and never grow, and
/// each entry is checked against the share recorded here so a change cannot
/// quietly make one of them worse. It held six before the glaze boundary
/// stopped being an instant `DryAll` (see `Painting::glaze`).
const KNOWN_EARLY_DRY: &[(&str, f32)] = &[
    ("moonlit-shoreline", 0.57),
    ("distant-mountains", 0.68),
    ("connected-shores", 0.62),
];

/// The share of its ticks at which the sheet last held water.
fn wet_until(scene: nocturne_watercolour_core::domain::Scene) -> f32 {
    let total = scene.timeline.total_ticks;
    let mut pb = Playback::new(CpuEngine::default(), scene, 1000.0)
        .unwrap()
        .with_tick_budget(0);
    let step = (total / 40).max(1);
    let mut last_wet = 0u32;
    let mut tick = 0u32;
    while tick < total {
        let advance = step.min(total - tick);
        pb.advance_ticks(advance).unwrap();
        tick += advance;
        let wet = pb
            .simulator()
            .grid()
            .expect("cpu grid")
            .wet
            .iter()
            .any(|&w| w > 0.0);
        if wet {
            last_wet = tick;
        }
    }
    last_wet as f32 / total.max(1) as f32
}

/// The tail has to be worth watching: the sheet must still be giving up water
/// at the end, because that is when pigment leaves suspension and sets into
/// the paper. An artwork that is bone dry two thirds of the way through spends
/// its last second holding a finished picture.
fn assert_each_sheet_is_still_settling_at_the_end(ids: &[&str], detail: DetailLevel) {
    let palette = Palette::moonlight();
    let mut failed: Vec<String> = Vec::new();
    for &id in ids {
        let scene = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
        )
        .unwrap();
        let share = wet_until(scene);
        let known = KNOWN_EARLY_DRY.iter().find(|(k, _)| *k == id);
        println!(
            "{id:<24} wet until {share:.2} of its ticks{}",
            if known.is_some() {
                "  (known early)"
            } else {
                ""
            }
        );
        match known {
            Some((_, recorded)) => {
                if share >= MIN_WET_SHARE {
                    failed.push(format!(
                        "{id} now settles to {share:.2} and no longer belongs in \
                         KNOWN_EARLY_DRY — remove it"
                    ));
                } else if share < recorded - 0.05 {
                    failed.push(format!(
                        "{id} dries earlier than it did: {share:.2} against the recorded \
                         {recorded:.2}"
                    ));
                }
            }
            None if share < MIN_WET_SHARE => failed.push(format!(
                "{id} is dry at {share:.2} of its ticks, so its tail holds a finished \
                 frame instead of settling"
            )),
            None => {}
        }
    }
    assert!(failed.is_empty(), "{}", failed.join("\n  "));
}

/// Frames sampled evenly across the reveal's wall clock for the snap check.
const SNAP_FRAMES: usize = 16;
/// How much bigger the last frame-to-frame change may be than the typical one
/// before it reads as a jump rather than as settling.
const MAX_SNAP_RATIO: f32 = 3.0;

/// Nothing may jump on the last frame.
///
/// A timeline ends with an implicit `DryAll`, which settles everything still
/// suspended in one step. If the sheet is still holding pigment when it fires,
/// the artwork snaps to its finished state in the final frame — and it snaps
/// harder the more pigment is still riding, because suspended pigment renders
/// at `wet_pigment_visibility` and deposited pigment at full weight.
/// `selection-edge` used to deposit twenty-two times as much on its last tick
/// as it had over the whole tail before it.
///
/// Measured in the viewer's time, as the viewer sees it: the change between
/// the last two frames against the median change across the reveal.
fn assert_none_snaps_to_its_final_state(ids: &[&str], detail: DetailLevel) {
    let palette = Palette::moonlight();
    let mut failed: Vec<String> = Vec::new();
    for &id in ids {
        let scene = ArtworkCatalogue::by_id_for(
            id,
            Seed(11),
            &palette,
            DEFAULT_INTENSITY,
            detail,
            Background::Transparent,
        )
        .unwrap();
        let aspect = scene.size_hint.height as f32 / scene.size_hint.width as f32;
        let (w, h) = (192u32, ((192.0 * aspect).round() as u32).max(8));
        let mut pb = Playback::new(CpuEngine::default(), scene, 3000.0)
            .unwrap()
            .with_tick_budget(0);

        let mut prev: Option<Image> = None;
        let mut steps: Vec<f32> = Vec::new();
        for f in 0..SNAP_FRAMES {
            let progress = f as f32 / (SNAP_FRAMES - 1) as f32;
            if progress >= 1.0 {
                pb.finish_immediately().unwrap();
            } else {
                pb.seek_progress(progress).unwrap();
            }
            let frame = pb.simulator().render(w, h).unwrap();
            if let Some(p) = prev {
                steps.push(body_mae(&frame, &p));
            }
            prev = Some(frame);
        }

        let last = *steps.last().unwrap();
        let mut sorted: Vec<f32> = steps[..steps.len() - 1].to_vec();
        sorted.sort_by(f32::total_cmp);
        let median = sorted[sorted.len() / 2].max(1e-6);
        let ratio = last / median;
        println!("{id:<24} last step {last:.5}, median {median:.5}, ratio {ratio:.2}");
        if ratio > MAX_SNAP_RATIO {
            failed.push(format!(
                "{id}: the last frame moved {ratio:.1}x the typical frame. The sheet is \
                 still holding pigment when the timeline's DryAll fires — size the \
                 settle so it reaches dry first"
            ));
        }
    }
    assert!(failed.is_empty(), "{}", failed.join("\n  "));
}

/// The artwork the default run checks at [`REPRESENTATIVE_DETAIL`], in
/// seconds, so the gates cannot rot between full sweeps: the one this file was
/// written for.
const REPRESENTATIVE: &[&str] = &["alarm-bell"];
const REPRESENTATIVE_DETAIL: DetailLevel = DetailLevel::Medium;
/// An entry of [`KNOWN_EARLY_DRY`], so the default run takes the ratchet path
/// as well. Only the settle check gets it: a wide scene is the slowest thing
/// in the catalogue to render.
const REPRESENTATIVE_EARLY_DRY: &str = "moonlit-shoreline";
/// The staged artwork the default run steps through stop by stop.
const REPRESENTATIVE_STAGED: &str = "hub-dawn-ridges";

#[test]
#[ignore = "slow: every catalogue id at Large detail; run with cargo test --release -- --ignored"]
fn every_artwork_survives_its_own_reveal() {
    assert_each_survives_its_own_reveal(ArtworkCatalogue::ids(), DetailLevel::Large);
}

#[test]
fn representative_artworks_survive_their_own_reveal() {
    assert_each_survives_its_own_reveal(REPRESENTATIVE, REPRESENTATIVE_DETAIL);
}

#[test]
#[ignore = "slow: every catalogue id; run with cargo test --release -- --ignored"]
fn artworks_survive_their_reveal_on_a_dark_ground() {
    assert_each_survives_its_reveal_on_a_dark_ground(ArtworkCatalogue::ids());
}

#[test]
fn representative_artworks_survive_their_reveal_on_a_dark_ground() {
    assert_each_survives_its_reveal_on_a_dark_ground(REPRESENTATIVE);
}

#[test]
#[ignore = "slow: every catalogue id at Large detail; run with cargo test --release -- --ignored"]
fn every_artwork_is_still_arriving_a_quarter_of_the_way_in() {
    assert_each_is_still_arriving_a_quarter_of_the_way_in(
        ArtworkCatalogue::ids(),
        DetailLevel::Large,
    );
}

#[test]
fn representative_artworks_are_still_arriving_a_quarter_of_the_way_in() {
    assert_each_is_still_arriving_a_quarter_of_the_way_in(REPRESENTATIVE, REPRESENTATIVE_DETAIL);
}

#[test]
#[ignore = "slow: every catalogue id at Large detail; run with cargo test --release -- --ignored"]
fn the_sheet_is_still_settling_at_the_end() {
    assert_each_sheet_is_still_settling_at_the_end(ArtworkCatalogue::ids(), DetailLevel::Large);
}

#[test]
fn representative_sheets_are_still_settling_at_the_end() {
    assert_each_sheet_is_still_settling_at_the_end(
        &[REPRESENTATIVE[0], REPRESENTATIVE_EARLY_DRY],
        REPRESENTATIVE_DETAIL,
    );
}

#[test]
#[ignore = "slow: every catalogue id at Large detail; run with cargo test --release -- --ignored"]
fn no_artwork_snaps_to_its_final_state() {
    assert_none_snaps_to_its_final_state(ArtworkCatalogue::ids(), DetailLevel::Large);
}

#[test]
fn no_representative_artwork_snaps_to_its_final_state() {
    assert_none_snaps_to_its_final_state(REPRESENTATIVE, REPRESENTATIVE_DETAIL);
}

#[test]
#[ignore = "slow: every staged catalogue id at Large detail; run with cargo test --release -- --ignored"]
fn every_staged_artwork_lays_its_stages_in_order() {
    let staged: Vec<&str> = ArtworkCatalogue::ids()
        .iter()
        .copied()
        .filter(|id| ArtworkCatalogue::stages(id).is_some())
        .collect();
    assert_each_stage_is_laid_in_order(&staged, DetailLevel::Large);
}

#[test]
fn the_representative_staged_artwork_lays_its_stages_in_order() {
    assert_each_stage_is_laid_in_order(&[REPRESENTATIVE_STAGED], REPRESENTATIVE_DETAIL);
}
