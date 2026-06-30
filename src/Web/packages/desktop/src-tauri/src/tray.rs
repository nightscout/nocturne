//! Windows system tray for the glucose companion.
//!
//! The icon is re-rasterized in Rust (not the webview) on every poll: the main window can be
//! hidden, and a hidden webview gets throttled, so it can't drive a live tray icon. The tooltip
//! carries the value + trend arrow + age as a text complement to the drawn icon.

use crate::glucose_poll::CurrentBg;
use tauri::menu::{Menu, MenuItem};
use tauri::tray::{MouseButton, MouseButtonState, TrayIconBuilder, TrayIconEvent};
use tauri::{AppHandle, Manager};

pub const TRAY_ID: &str = "nocturne-companion";
pub const MAIN_WINDOW_LABEL: &str = "main";

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
    let font_bytes = std::fs::read(FONT_PATH).ok()?;
    let font = fontdue::Font::from_bytes(font_bytes, fontdue::FontSettings::default()).ok()?;

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

fn show_main_window(app: &AppHandle) {
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
pub fn update_tray(app: &AppHandle, current: Option<&CurrentBg>) {
    let tray = match app.tray_by_id(TRAY_ID) {
        Some(t) => t,
        None => return,
    };

    let _ = tray.set_tooltip(Some(tooltip_text(current)));

    let (text, bg_color) = match current {
        Some(c) => (to_display(c.sgv_mgdl), status_color(status_from_mgdl(c.sgv_mgdl))),
        None => ("--".to_string(), COLOR_NO_DATA),
    };

    if let Some((rgba, w, h)) = rasterize(&text, bg_color) {
        let _ = tray.set_icon(Some(tauri::image::Image::new_owned(rgba, w, h)));
    }
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
            direction: Some("FortyFiveUp".to_string()),
            mills: 0,
        };
        let t = tooltip_text(Some(&bg));
        assert!(t.starts_with("5.5 mmol/L"));
        assert!(t.contains('\u{2197}'));
    }
}
