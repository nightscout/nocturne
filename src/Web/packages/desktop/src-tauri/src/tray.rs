//! Windows system tray for the glucose companion.
//!
//! The icon is re-rasterized in Rust (not the webview) on every poll: the main window can be
//! hidden, and a hidden webview gets throttled, so it can't drive a live tray icon. The tooltip
//! carries the value + trend arrow + age as a text complement to the drawn icon.
//!
//! The tray also actuates the `tray_flash` device capability: while any alert excursion requests it,
//! a background task pulses the icon between the normal glucose tile and an attention variant. Flash
//! ownership is coordinated with the glucose poll via shared state so the two don't fight over the
//! icon and the normal tile is restored when the last flashing excursion clears.

use crate::glucose_poll::CurrentBg;
use std::collections::HashSet;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Mutex, OnceLock};
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager};

pub const TRAY_ID: &str = "nocturne-companion";
pub const MAIN_WINDOW_LABEL: &str = "main";

/// Attention-tile fill for the flash "on" frame — a high-contrast alert red drawn behind the value.
const COLOR_ATTENTION: (u8, u8, u8) = (0xE0, 0x53, 0x3D);

/// Flash pulse cadence: the icon alternates between the normal tile and the attention tile every
/// this many milliseconds while flashing is active.
const FLASH_INTERVAL_MS: u64 = 600;

/// Display unit. mmol/L is the default (1 dp); switch to "mg/dL" (0 dp) by changing this constant.
const TRAY_UNIT: &str = "mmol/L";
const MMOL_PER_MGDL: f64 = 18.0182;

/// Segoe UI Bold — present on every Win11 install. Read at runtime; never bundled/committed.
const FONT_PATH: &str = r"C:\Windows\Fonts\segoeuib.ttf";

const ICON_SIZE: u32 = 32;
/// Corner radius of the favicon-style rounded-rect tile (mirrors `@nocturne/ui` glucose-icon).
const ICON_RADIUS: f32 = 6.0;

/// Glucose status thresholds (mg/dL). A 3-state model (Low / InRange / High) to match the
/// taskbar mod, not the web's 5-state. Kept aligned with the `@nocturne/ui` glucose palette.
const LOW_THRESHOLD_MGDL: f64 = 70.0;
const HIGH_THRESHOLD_MGDL: f64 = 180.0;

/// Status-tile fill colors as (R, G, B). Mirrors the mod defaults / `@nocturne/ui` palette.
const COLOR_IN_RANGE: (u8, u8, u8) = (0x36, 0xC7, 0x6A);
const COLOR_HIGH: (u8, u8, u8) = (0xE6, 0xB8, 0x00);
const COLOR_LOW: (u8, u8, u8) = (0xE0, 0x53, 0x3D);
/// No-data (`--`) state.
const COLOR_NO_DATA: (u8, u8, u8) = (0x6B, 0x72, 0x80);
const COLOR_FG: (u8, u8, u8) = (0xFF, 0xFF, 0xFF);

#[derive(Debug, Clone, Copy, PartialEq, Eq)]
enum GlucoseStatus {
    Low,
    InRange,
    High,
}

/// Shared tray state, coordinating the glucose poll and the flash task.
///
/// `last_reading` caches the latest `CurrentBg` so the flash task can re-render the normal tile
/// between attention frames without re-polling, and a glucose poll that lands mid-flash just updates
/// the cache. `flashing` holds the excursion ids currently requesting a flash; when it is non-empty a
/// single background task owns the icon (see `flash_active`).
struct TrayState {
    last_reading: Option<CurrentBg>,
    flashing: HashSet<String>,
}

fn tray_state() -> &'static Mutex<TrayState> {
    static STATE: OnceLock<Mutex<TrayState>> = OnceLock::new();
    STATE.get_or_init(|| {
        Mutex::new(TrayState {
            last_reading: None,
            flashing: HashSet::new(),
        })
    })
}

/// Monotonic flash generation. Every start/stop transition bumps it; a pulse task captures the value
/// it was spawned under and exits once the global moves past it, so stale tasks can neither
/// accumulate alongside a newer one nor paint an attention frame after a stop's restore render.
static FLASH_GENERATION: AtomicU64 = AtomicU64::new(0);

fn status_from_mgdl(sgv_mgdl: f64) -> GlucoseStatus {
    if sgv_mgdl < LOW_THRESHOLD_MGDL {
        GlucoseStatus::Low
    } else if sgv_mgdl > HIGH_THRESHOLD_MGDL {
        GlucoseStatus::High
    } else {
        GlucoseStatus::InRange
    }
}

fn status_color(status: GlucoseStatus) -> (u8, u8, u8) {
    match status {
        GlucoseStatus::Low => COLOR_LOW,
        GlucoseStatus::InRange => COLOR_IN_RANGE,
        GlucoseStatus::High => COLOR_HIGH,
    }
}

/// mg/dL to the configured display unit, formatted for the icon (mmol/L 1dp, mg/dL whole).
fn to_display(sgv_mgdl: f64) -> String {
    if TRAY_UNIT == "mg/dL" {
        format!("{:.0}", sgv_mgdl.round())
    } else {
        format!("{:.1}", sgv_mgdl / MMOL_PER_MGDL)
    }
}

/// Dexcom-style direction string to an arrow glyph for the tooltip.
fn direction_arrow(direction: Option<&str>) -> &'static str {
    match direction.unwrap_or("") {
        "DoubleUp" => "\u{2191}\u{2191}",
        "SingleUp" => "\u{2191}",
        "FortyFiveUp" => "\u{2197}",
        "Flat" => "\u{2192}",
        "FortyFiveDown" => "\u{2198}",
        "SingleDown" => "\u{2193}",
        "DoubleDown" => "\u{2193}\u{2193}",
        _ => "",
    }
}

/// Human "N ago" from an epoch-millis reading time. Empty if `mills` is unset/in the future.
fn age_label(mills: i64) -> String {
    if mills <= 0 {
        return String::new();
    }
    let now_ms = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as i64)
        .unwrap_or(0);
    let secs = (now_ms - mills) / 1000;
    if secs < 0 {
        String::new()
    } else if secs < 60 {
        "now".to_string()
    } else if secs < 3600 {
        format!("{}m ago", secs / 60)
    } else {
        format!("{}h ago", secs / 3600)
    }
}

/// Tooltip text, e.g. `5.8 mmol/L ↗ · 3m ago`, or `No data` when unlinked / no reading yet.
fn tooltip_text(current: Option<&CurrentBg>) -> String {
    match current {
        Some(c) => {
            let mut s = format!("{} {}", to_display(c.sgv_mgdl), TRAY_UNIT);
            let arrow = direction_arrow(c.direction.as_deref());
            if !arrow.is_empty() {
                s.push(' ');
                s.push_str(arrow);
            }
            let age = age_label(c.mills);
            if !age.is_empty() {
                s.push_str(" \u{00b7} ");
                s.push_str(&age);
            }
            s
        }
        None => "No data".to_string(),
    }
}

/// Fills the `ICON_SIZE`-square RGBA `buf` with a radius-`ICON_RADIUS` rounded rect in `color`.
/// Each corner is a quarter circle centered `radius` in from the corner; pixels beyond that radius
/// stay transparent. Hard mask, no anti-aliasing.
fn fill_rounded_rect(buf: &mut [u8], color: (u8, u8, u8)) {
    let n = ICON_SIZE as i32;
    let r = ICON_RADIUS;
    let (cr, cg, cb) = color;
    for y in 0..n {
        for x in 0..n {
            let px = x as f32 + 0.5;
            let py = y as f32 + 0.5;
            let inside = if px < r && py < r {
                ((px - r).powi(2) + (py - r).powi(2)).sqrt() <= r
            } else if px > n as f32 - r && py < r {
                ((px - (n as f32 - r)).powi(2) + (py - r).powi(2)).sqrt() <= r
            } else if px < r && py > n as f32 - r {
                ((px - r).powi(2) + (py - (n as f32 - r)).powi(2)).sqrt() <= r
            } else if px > n as f32 - r && py > n as f32 - r {
                ((px - (n as f32 - r)).powi(2) + (py - (n as f32 - r)).powi(2)).sqrt() <= r
            } else {
                true
            };
            if !inside {
                continue;
            }
            let idx = ((y as u32 * ICON_SIZE + x as u32) * 4) as usize;
            buf[idx] = cr;
            buf[idx + 1] = cg;
            buf[idx + 2] = cb;
            buf[idx + 3] = 255;
        }
    }
}

/// Rasterizes a favicon-style tile: a radius-6 rounded rect filled with `bg_color`, with `text`
/// centered on top in `COLOR_FG` (fontdue coverage blended as alpha over the fill). Returns
/// `(rgba, width, height)`. `None` if the font can't be read/parsed — callers then leave the
/// default icon and just update the tooltip.
fn rasterize(text: &str, bg_color: (u8, u8, u8)) -> Option<(Vec<u8>, u32, u32)> {
    let font = font()?;

    // Font size by display-text length, mirroring the web favicon (`@nocturne/ui` glucose-icon):
    // <=2 chars -> 18px, <=3 -> 14px, else 11px.
    let px = match text.chars().count() {
        0..=2 => 18.0,
        3 => 14.0,
        _ => 11.0,
    };

    // Track the vertical extent across glyphs so the whole run can be centered.
    struct Placed {
        bitmap: Vec<u8>,
        w: usize,
        h: usize,
        x: i32,
        top: i32, // y of the glyph's top relative to the baseline (negative = above)
    }
    let mut placed = Vec::new();
    let mut pen_x = 0.0f32;
    let mut min_top = i32::MAX;
    let mut max_bottom = i32::MIN;

    for ch in text.chars() {
        let (metrics, bitmap) = font.rasterize(ch, px);
        let x = pen_x.round() as i32 + metrics.xmin;
        // fontdue ymin is the offset of the bitmap's bottom from the baseline (y-up).
        let top = -(metrics.ymin + metrics.height as i32);
        if metrics.width > 0 && metrics.height > 0 {
            min_top = min_top.min(top);
            max_bottom = max_bottom.max(top + metrics.height as i32);
        }
        placed.push(Placed {
            bitmap,
            w: metrics.width,
            h: metrics.height,
            x,
            top,
        });
        pen_x += metrics.advance_width;
    }

    if placed.is_empty() || min_top == i32::MAX {
        return None;
    }

    let text_w = pen_x.round() as i32;
    let text_h = max_bottom - min_top;
    let canvas = ICON_SIZE as i32;
    let offset_x = (canvas - text_w) / 2;
    let offset_y = (canvas - text_h) / 2 - min_top;

    let mut rgba = vec![0u8; (ICON_SIZE * ICON_SIZE * 4) as usize];
    fill_rounded_rect(&mut rgba, bg_color);

    let (fr, fg_, fb) = COLOR_FG;
    for g in &placed {
        for gy in 0..g.h {
            for gx in 0..g.w {
                let coverage = g.bitmap[gy * g.w + gx];
                if coverage == 0 {
                    continue;
                }
                let px_x = offset_x + g.x + gx as i32;
                let px_y = offset_y + g.top + gy as i32;
                if px_x < 0 || px_y < 0 || px_x >= canvas || px_y >= canvas {
                    continue;
                }
                let idx = ((px_y as u32 * ICON_SIZE + px_x as u32) * 4) as usize;
                let a = coverage as u32;
                let inv = 255 - a;
                rgba[idx] = ((fr as u32 * a + rgba[idx] as u32 * inv) / 255) as u8;
                rgba[idx + 1] = ((fg_ as u32 * a + rgba[idx + 1] as u32 * inv) / 255) as u8;
                rgba[idx + 2] = ((fb as u32 * a + rgba[idx + 2] as u32 * inv) / 255) as u8;
                // Keep the tile's full alpha (text sits on an opaque tile).
                rgba[idx + 3] = rgba[idx + 3].max(coverage);
            }
        }
    }

    Some((rgba, ICON_SIZE, ICON_SIZE))
}

/// The parsed tray font, read and parsed once — `rasterize` runs every poll and every 600ms flash
/// frame, so the file read + parse must not repeat per call. A read/parse failure is remembered as
/// `None` (the font set doesn't change while Windows is up).
fn font() -> Option<&'static fontdue::Font> {
    static FONT: OnceLock<Option<fontdue::Font>> = OnceLock::new();
    FONT.get_or_init(|| {
        let bytes = std::fs::read(FONT_PATH).ok()?;
        fontdue::Font::from_bytes(bytes, fontdue::FontSettings::default()).ok()
    })
    .as_ref()
}

pub(crate) fn show_main_window(app: &AppHandle) {
    if let Some(window) = app.get_webview_window(MAIN_WINDOW_LABEL) {
        let _ = window.show();
        let _ = window.unminimize();
        let _ = window.set_focus();
    }
}

/// Builds the tray icon + menu. Called once from the Tauri `.setup` hook.
pub fn build_tray(app: &AppHandle) -> tauri::Result<()> {
    let show = MenuItem::with_id(app, "show", "Show", true, None::<&str>)?;
    let quit = MenuItem::with_id(app, "quit", "Quit", true, None::<&str>)?;
    let menu = Menu::with_items(app, &[&show, &quit])?;

    let mut builder = TrayIconBuilder::with_id(TRAY_ID)
        .tooltip("Nocturne Companion")
        .menu(&menu)
        .show_menu_on_left_click(false)
        .on_menu_event(|app, event| match event.id().as_ref() {
            "show" => show_main_window(app),
            "quit" => app.exit(0),
            _ => {}
        })
        .on_tray_icon_event(|tray, event| {
            if let TrayIconEvent::Click {
                button: MouseButton::Left,
                button_state: MouseButtonState::Up,
                ..
            } = event
            {
                show_main_window(tray.app_handle());
            }
        });

    if let Some(icon) = app.default_window_icon() {
        builder = builder.icon(icon.clone());
    }

    builder.build(app)?;
    Ok(())
}

/// Re-renders the tray icon for `current` (or `--` when `None`) and refreshes the tooltip. A
/// font/render failure leaves the existing icon and still updates the tooltip; never panics.
///
/// Caches `current` as the last reading. While a flash is active the flash task owns the icon, so
/// only the tooltip is refreshed here — the icon is left to the pulse (which re-reads the cache).
pub fn update_tray(app: &AppHandle, current: Option<&CurrentBg>) {
    let flashing = match tray_state().lock() {
        Ok(mut state) => {
            state.last_reading = current.cloned();
            !state.flashing.is_empty()
        }
        Err(_) => false,
    };

    let tray = match app.tray_by_id(TRAY_ID) {
        Some(t) => t,
        None => return,
    };
    let _ = tray.set_tooltip(Some(tooltip_text(current)));

    // While flashing, the pulse task owns the icon (it re-reads the cache just updated above).
    if flashing {
        return;
    }
    render_icon(app, current, false);
}

/// Renders the tile for `current` (`--` when `None`) and sets it as the tray icon. When `attention`
/// is set, the tile uses the attention fill (the flash "on" frame) instead of the glucose-status
/// color, keeping the value legible. A render failure leaves the existing icon.
fn render_icon(app: &AppHandle, current: Option<&CurrentBg>, attention: bool) {
    let tray = match app.tray_by_id(TRAY_ID) {
        Some(t) => t,
        None => return,
    };

    let (text, bg_color) = match current {
        Some(c) => {
            let color = if attention { COLOR_ATTENTION } else { status_color(status_from_mgdl(c.sgv_mgdl)) };
            (to_display(c.sgv_mgdl), color)
        }
        None => ("--".to_string(), if attention { COLOR_ATTENTION } else { COLOR_NO_DATA }),
    };

    if let Some((rgba, w, h)) = rasterize(&text, bg_color) {
        let _ = tray.set_icon(Some(tauri::image::Image::new_owned(rgba, w, h)));
    }
}

/// The effect of a `set_tray_flash` call on the shared flashing set.
#[derive(Debug, PartialEq, Eq)]
enum FlashTransition {
    /// The first flashing excursion was added — start the pulse task.
    Started,
    /// The last flashing excursion was removed — stop pulsing and restore the normal icon.
    Stopped,
    /// The set changed but flashing was already active (or already idle) — no task change needed.
    Unchanged,
}

/// Adds or removes `excursion_id` from the flashing set and reports the resulting task transition.
/// Idempotent: adding an already-flashing id or removing an absent one is `Unchanged`. Keyed on
/// excursion id so re-running a reconcile with the same active set doesn't restart the pulse.
fn apply_flash_change(state: &mut TrayState, excursion_id: &str, on: bool) -> FlashTransition {
    let was_flashing = !state.flashing.is_empty();
    if on {
        state.flashing.insert(excursion_id.to_string());
    } else {
        state.flashing.remove(excursion_id);
    }
    let now_flashing = !state.flashing.is_empty();

    match (was_flashing, now_flashing) {
        (false, true) => FlashTransition::Started,
        (true, false) => FlashTransition::Stopped,
        _ => FlashTransition::Unchanged,
    }
}

/// What `set_tray_flash` must do once the state lock is released. Decided (and the generation
/// bumped) while the lock is still held, so the (flashing set, generation) pair changes atomically:
/// a concurrent `stop_all_flashes` cannot interleave between a start's set insert and its generation
/// bump and leave a pulse task pinned to the newest generation with an empty flashing set.
enum FlashAction {
    /// Spawn the pulse task pinned to this (already bumped) generation.
    SpawnPulse(u64),
    /// The pulse was retired (generation already bumped); repaint the normal tile from this cached
    /// reading.
    Restore(Option<CurrentBg>),
    None,
}

/// Applies `transition` to the flash generation and reports what the caller should do after
/// releasing the state lock (the pulse task is spawned and the icon rendered outside it).
fn plan_flash_action(
    state: &TrayState,
    transition: FlashTransition,
    generation: &AtomicU64,
) -> FlashAction {
    match transition {
        FlashTransition::Started => {
            // Bumping the generation retires any previous pulse task before its next frame; the new
            // task owns the icon from here.
            FlashAction::SpawnPulse(generation.fetch_add(1, Ordering::SeqCst) + 1)
        }
        FlashTransition::Stopped => {
            generation.fetch_add(1, Ordering::SeqCst);
            FlashAction::Restore(state.last_reading.clone())
        }
        FlashTransition::Unchanged => FlashAction::None,
    }
}

/// Starts or stops flashing the tray icon for `excursion_id`. Called from the reconcile path when an
/// intent's capabilities include (or no longer include) `tray_flash`. Idempotent per excursion id:
/// the first active excursion starts a single background pulse; the last one to clear stops it and
/// restores the normal glucose icon. The glucose poll defers to the pulse via the flashing set.
pub fn set_tray_flash(app: &AppHandle, excursion_id: &str, on: bool) {
    let action = {
        let mut state = match tray_state().lock() {
            Ok(s) => s,
            Err(_) => return,
        };
        let transition = apply_flash_change(&mut state, excursion_id, on);
        plan_flash_action(&state, transition, &FLASH_GENERATION)
    };

    // Outside the lock: a stop that lands between the plan and the spawn has already bumped past
    // the planned generation, so the new task exits before painting a frame.
    match action {
        FlashAction::SpawnPulse(generation) => spawn_flash_task(app.clone(), generation),
        FlashAction::Restore(reading) => render_icon(app, reading.as_ref(), false),
        FlashAction::None => {}
    }
}

/// Stops every flash at once (all actuations withdrawn — e.g. unlink/relink): clears the flashing
/// set, retires the pulse task, and restores the normal icon. Idempotent: the generation is bumped
/// even when nothing is flashing, so a pulse task orphaned by any interleaving is retired rather
/// than left pulsing forever, and the extra restore repaint of the normal tile is harmless.
pub fn stop_all_flashes(app: &AppHandle) {
    let reading = {
        let mut state = match tray_state().lock() {
            Ok(s) => s,
            Err(_) => return,
        };
        stop_all_and_bump(&mut state, &FLASH_GENERATION)
    };
    render_icon(app, reading.as_ref(), false);
}

/// The under-lock half of `stop_all_flashes`: clears the flashing set, bumps the generation
/// unconditionally, and returns the cached reading for the restore repaint.
fn stop_all_and_bump(state: &mut TrayState, generation: &AtomicU64) -> Option<CurrentBg> {
    state.flashing.clear();
    generation.fetch_add(1, Ordering::SeqCst);
    state.last_reading.clone()
}

/// Background task that pulses the icon between the normal and attention tiles. Captures the
/// generation it was spawned under and exits once the global generation moves past it (a stop, or a
/// newer start). Re-reads the cached reading each frame so a glucose poll landing mid-flash is
/// reflected. A stop can race one in-flight render, so the post-render generation check repaints the
/// normal tile rather than leaving the attention frame as the last one painted.
fn spawn_flash_task(app: AppHandle, generation: u64) {
    tauri::async_runtime::spawn(async move {
        let mut attention = true;
        while FLASH_GENERATION.load(Ordering::SeqCst) == generation {
            let current = tray_state().lock().ok().and_then(|s| s.last_reading.clone());
            render_icon(&app, current.as_ref(), attention);
            if FLASH_GENERATION.load(Ordering::SeqCst) != generation {
                let current = tray_state().lock().ok().and_then(|s| s.last_reading.clone());
                render_icon(&app, current.as_ref(), false);
                break;
            }
            attention = !attention;
            tokio::time::sleep(std::time::Duration::from_millis(FLASH_INTERVAL_MS)).await;
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn mmol_display_rounds_to_one_dp() {
        assert_eq!(to_display(100.0), "5.5");
        assert_eq!(to_display(180.0), "10.0");
    }

    #[test]
    fn direction_maps_to_arrow() {
        assert_eq!(direction_arrow(Some("Flat")), "\u{2192}");
        assert_eq!(direction_arrow(Some("SingleUp")), "\u{2191}");
        assert_eq!(direction_arrow(Some("Bogus")), "");
        assert_eq!(direction_arrow(None), "");
    }

    #[test]
    fn tooltip_handles_no_data() {
        assert_eq!(tooltip_text(None), "No data");
    }

    #[test]
    fn status_classifies_by_mgdl_thresholds() {
        assert_eq!(status_from_mgdl(69.9), GlucoseStatus::Low);
        assert_eq!(status_from_mgdl(70.0), GlucoseStatus::InRange); // boundary: not low
        assert_eq!(status_from_mgdl(120.0), GlucoseStatus::InRange);
        assert_eq!(status_from_mgdl(180.0), GlucoseStatus::InRange); // boundary: not high
        assert_eq!(status_from_mgdl(180.1), GlucoseStatus::High);
    }

    #[test]
    fn status_maps_to_palette() {
        assert_eq!(status_color(GlucoseStatus::Low), COLOR_LOW);
        assert_eq!(status_color(GlucoseStatus::InRange), COLOR_IN_RANGE);
        assert_eq!(status_color(GlucoseStatus::High), COLOR_HIGH);
        // Palette matches the mod defaults / @nocturne/ui glucose palette.
        assert_eq!(COLOR_IN_RANGE, (0x36, 0xC7, 0x6A));
        assert_eq!(COLOR_HIGH, (0xE6, 0xB8, 0x00));
        assert_eq!(COLOR_LOW, (0xE0, 0x53, 0x3D));
    }

    #[test]
    fn rounded_rect_corners_are_transparent_and_center_filled() {
        let mut buf = vec![0u8; (ICON_SIZE * ICON_SIZE * 4) as usize];
        fill_rounded_rect(&mut buf, COLOR_IN_RANGE);
        // Top-left pixel (0,0) is outside the radius-6 corner -> transparent.
        assert_eq!(buf[3], 0);
        // Center pixel is filled at full alpha with the tile color.
        let mid = ((ICON_SIZE / 2 * ICON_SIZE + ICON_SIZE / 2) * 4) as usize;
        assert_eq!(buf[mid], 0x36);
        assert_eq!(buf[mid + 1], 0xC7);
        assert_eq!(buf[mid + 2], 0x6A);
        assert_eq!(buf[mid + 3], 255);
    }

    #[test]
    fn tooltip_includes_value_and_arrow() {
        let bg = CurrentBg {
            sgv_mgdl: 100.0,
            delta_mgdl: None,
            direction: Some("FortyFiveUp".to_string()),
            mills: 0,
        };
        let t = tooltip_text(Some(&bg));
        assert!(t.starts_with("5.5 mmol/L"));
        assert!(t.contains('\u{2197}'));
    }

    fn fresh_state() -> TrayState {
        TrayState { last_reading: None, flashing: HashSet::new() }
    }

    #[test]
    fn first_flash_starts_and_duplicate_is_unchanged() {
        let mut s = fresh_state();
        assert_eq!(apply_flash_change(&mut s, "a", true), FlashTransition::Started);
        // Same excursion again → already flashing, no task change.
        assert_eq!(apply_flash_change(&mut s, "a", true), FlashTransition::Unchanged);
        assert!(s.flashing.contains("a"));
    }

    #[test]
    fn flash_stops_only_when_last_excursion_clears() {
        let mut s = fresh_state();
        apply_flash_change(&mut s, "a", true);
        // A second flashing excursion keeps the task running.
        assert_eq!(apply_flash_change(&mut s, "b", true), FlashTransition::Unchanged);
        // Removing one of two → still flashing.
        assert_eq!(apply_flash_change(&mut s, "a", false), FlashTransition::Unchanged);
        // Removing the last → stop.
        assert_eq!(apply_flash_change(&mut s, "b", false), FlashTransition::Stopped);
        assert!(s.flashing.is_empty());
    }

    #[test]
    fn removing_absent_excursion_is_unchanged() {
        let mut s = fresh_state();
        assert_eq!(apply_flash_change(&mut s, "ghost", false), FlashTransition::Unchanged);
        assert!(s.flashing.is_empty());
    }

    // ── flash generation: (set, generation) must change atomically ───────────────────────

    fn reading(sgv_mgdl: f64) -> CurrentBg {
        CurrentBg { sgv_mgdl, delta_mgdl: None, direction: None, mills: 0 }
    }

    #[test]
    fn started_bumps_generation_and_pins_pulse_to_it() {
        let generation = AtomicU64::new(7);
        let s = fresh_state();
        match plan_flash_action(&s, FlashTransition::Started, &generation) {
            FlashAction::SpawnPulse(g) => assert_eq!(g, 8),
            _ => panic!("expected SpawnPulse"),
        }
        assert_eq!(generation.load(Ordering::SeqCst), 8);
    }

    #[test]
    fn stopped_bumps_generation_and_restores_cached_reading() {
        let generation = AtomicU64::new(3);
        let mut s = fresh_state();
        s.last_reading = Some(reading(120.0));
        match plan_flash_action(&s, FlashTransition::Stopped, &generation) {
            FlashAction::Restore(Some(r)) => assert_eq!(r.sgv_mgdl, 120.0),
            _ => panic!("expected Restore with the cached reading"),
        }
        // The bump retires the pulse task even before the restore render runs.
        assert_eq!(generation.load(Ordering::SeqCst), 4);
    }

    #[test]
    fn unchanged_leaves_generation_alone() {
        let generation = AtomicU64::new(5);
        let s = fresh_state();
        assert!(matches!(
            plan_flash_action(&s, FlashTransition::Unchanged, &generation),
            FlashAction::None
        ));
        assert_eq!(generation.load(Ordering::SeqCst), 5);
    }

    #[test]
    fn stop_all_bumps_even_when_nothing_is_flashing() {
        // The idempotent-stop guarantee: a stop racing a start can observe an empty set, but the
        // unconditional bump still retires whatever pulse task the start spawned.
        let generation = AtomicU64::new(9);
        let mut s = fresh_state();
        stop_all_and_bump(&mut s, &generation);
        assert_eq!(generation.load(Ordering::SeqCst), 10);
    }

    #[test]
    fn stop_all_clears_set_and_returns_reading_for_restore() {
        let generation = AtomicU64::new(0);
        let mut s = fresh_state();
        apply_flash_change(&mut s, "a", true);
        apply_flash_change(&mut s, "b", true);
        s.last_reading = Some(reading(90.0));
        let r = stop_all_and_bump(&mut s, &generation);
        assert!(s.flashing.is_empty());
        assert_eq!(r.map(|c| c.sgv_mgdl), Some(90.0));
        assert_eq!(generation.load(Ordering::SeqCst), 1);
    }

    #[test]
    fn interleaved_stop_between_plan_and_spawn_retires_the_planned_pulse() {
        // set_tray_flash plans SpawnPulse(N) under the lock, releases it, and a stop_all lands
        // before the task's first frame: the stop's bump moves the generation past N, so the
        // pulse's `FLASH_GENERATION == generation` gate fails immediately — no orphan pulse.
        let generation = AtomicU64::new(0);
        let mut s = fresh_state();
        let transition = apply_flash_change(&mut s, "a", true);
        let planned = match plan_flash_action(&s, transition, &generation) {
            FlashAction::SpawnPulse(g) => g,
            _ => panic!("expected SpawnPulse"),
        };
        stop_all_and_bump(&mut s, &generation);
        assert_ne!(generation.load(Ordering::SeqCst), planned);
    }
}
