//! Lucide icons as watercolour scenes.
//!
//! A Lucide element list is flattened into subpaths in the mapped design
//! space. The hand-authored mapping transfers directly: a closed subpath is a
//! stencil-and-fill body, an open one is a wet-on-dry mark laid at the Lucide
//! stroke radius. An icon with no closed subpath (all arcs, like `fingerprint`)
//! has no silhouette, so its longest open subpath is laid as a fat body wash
//! instead.

use std::f32::consts::TAU;

use nocturne_watercolour_core::domain::{
    Background, Palette, Paper, PigmentRole, Point, Scene, Seed,
};
use serde::{Deserialize, Serialize};

use super::geometry::Frame;
use super::{
    DetailLevel, Painting, SQUARE, Shape, Style, brush, choreograph_scene, lift, role,
    stencil_body, style_for,
};

/// The Lucide stroke is 2 units on a 24 grid; half of it in the mapped frame
/// (`0.1..0.9`, so `0.8 / 24`). The hand-authored marks are a hair thinner
/// (0.028) but the watercolour needs heft. Exposed so the factor can be tuned.
pub const MARK_RADIUS_FACTOR: f32 = 1.0;

/// Flatness bound for curve flattening, in 24-grid units.
pub const FLATTEN_TOLERANCE: f32 = 0.25;
/// Marks shorter than this (mapped scene units) are dropped at `Small`, where
/// the whole icon is 48 px and a short mark reads as noise.
const SMALL_MIN_MARK_LENGTH: f32 = 0.10;
/// Marks kept at `Small`.
const SMALL_MAX_MARKS: usize = 4;
/// A silhouette drawn as a fat stroke (the no-closed-subpath case) is this many
/// times the mark radius, thick enough to read as a filled shape.
const BODY_STROKE_FACTOR: f32 = 2.5;

/// Per-icon tuning over the generic mapping: which open subpaths fill, how
/// heavy the marks are, and which pigments the two take. Travels with the icon
/// request, so it needs no rebuild to change.
#[derive(Debug, Clone, PartialEq)]
pub struct IconHints {
    /// Indices of open subpaths (in flattened order: element order, then
    /// subpath order within a path) to close and paint as bodies.
    pub fill: Vec<usize>,
    /// Multiplier on the Lucide stroke half-width for marks. 1.0 = as drawn.
    pub mark_radius: f32,
    /// Marks kept at `DetailLevel::Small` (longest first).
    pub small_marks: usize,
    /// Circles lifted out of the wet body before it glazes, in 24-grid units:
    /// `(cx, cy, r)`. For a key's hole and the like.
    pub holes: Vec<(f32, f32, f32)>,
    pub body_role: PigmentRole,
    pub mark_role: PigmentRole,
}

impl Default for IconHints {
    fn default() -> Self {
        IconHints {
            fill: Vec::new(),
            mark_radius: MARK_RADIUS_FACTOR,
            small_marks: SMALL_MAX_MARKS,
            holes: Vec::new(),
            body_role: PigmentRole::BaseWash,
            mark_role: PigmentRole::Shadow,
        }
    }
}

/// Serde mirror of [`IconHints`] for the icon request JSON. camelCase so the
/// TypeScript `IconHints` object serialises straight onto the wire; every
/// field is optional and an absent one keeps the default.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct IconHintsDoc {
    #[serde(default)]
    pub fill: Vec<usize>,
    #[serde(default)]
    pub mark_radius: Option<f32>,
    #[serde(default)]
    pub small_marks: Option<usize>,
    #[serde(default)]
    pub holes: Vec<[f32; 3]>,
    #[serde(default)]
    pub body_role: Option<String>,
    #[serde(default)]
    pub mark_role: Option<String>,
}

/// Parses the per-icon hints JSON; `""` or `{}` yields the default tuning.
pub fn parse_icon_hints(json: &str) -> Result<IconHints, IconParseError> {
    if json.trim().is_empty() {
        return Ok(IconHints::default());
    }
    let doc: IconHintsDoc =
        serde_json::from_str(json).map_err(|e| IconParseError::Json(e.to_string()))?;
    let holes = doc
        .holes
        .into_iter()
        .map(|[cx, cy, r]| (cx, cy, r))
        .collect();
    let body_role = match doc.body_role {
        Some(role) => parse_hint_role(&role)?,
        None => PigmentRole::BaseWash,
    };
    let mark_role = match doc.mark_role {
        Some(role) => parse_hint_role(&role)?,
        None => PigmentRole::Shadow,
    };
    Ok(IconHints {
        fill: doc.fill,
        mark_radius: doc.mark_radius.unwrap_or(MARK_RADIUS_FACTOR),
        small_marks: doc.small_marks.unwrap_or(SMALL_MAX_MARKS),
        holes,
        body_role,
        mark_role,
    })
}

fn parse_hint_role(name: &str) -> Result<PigmentRole, IconParseError> {
    Ok(match name {
        "base_wash" => PigmentRole::BaseWash,
        "shadow" => PigmentRole::Shadow,
        "accent" => PigmentRole::Accent,
        "glow" => PigmentRole::Glow,
        other => return Err(IconParseError::Role(other.to_string())),
    })
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum IconParseError {
    Json(String),
    UnknownElement(String),
    MissingAttribute(String),
    Number(String, String),
    Path(String),
    Role(String),
}

impl std::fmt::Display for IconParseError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            IconParseError::Json(e) => write!(f, "icon list is not JSON: {e}"),
            IconParseError::UnknownElement(kind) => write!(f, "unknown element kind {kind:?}"),
            IconParseError::MissingAttribute(a) => write!(f, "element missing attribute {a}"),
            IconParseError::Number(a, e) => write!(f, "attribute {a} is not a number: {e}"),
            IconParseError::Path(e) => write!(f, "bad path data: {e}"),
            IconParseError::Role(role) => write!(f, "unknown pigment role {role:?}"),
        }
    }
}

impl std::error::Error for IconParseError {}

/// One element of the Lucide list, as `["circle", {"cx": "12", ...}]`.
#[derive(Debug, Clone, PartialEq)]
pub enum IconNode {
    Path {
        d: String,
    },
    Circle {
        cx: f32,
        cy: f32,
        r: f32,
    },
    Rect {
        x: f32,
        y: f32,
        width: f32,
        height: f32,
        rx: f32,
        ry: f32,
    },
    Line {
        x1: f32,
        y1: f32,
        x2: f32,
        y2: f32,
    },
    Ellipse {
        cx: f32,
        cy: f32,
        rx: f32,
        ry: f32,
    },
    Polyline(Vec<(f32, f32)>),
    Polygon(Vec<(f32, f32)>),
}

/// A flattened polyline in the 24-grid space, tagged by whether it closes.
#[derive(Debug, Clone, PartialEq)]
pub struct SubPath {
    pub points: Vec<(f32, f32)>,
    pub closed: bool,
}

impl SubPath {
    /// Arc length in the current space, for the `Small` gating.
    pub fn length(&self) -> f32 {
        self.points.windows(2).map(|w| len(w[0], w[1])).sum()
    }
}

/// Parses the Lucide element list (the JSON array form shipped in the icon
/// `.svelte` files).
pub fn parse_icon_elements(json: &str) -> Result<Vec<IconNode>, IconParseError> {
    let list: Vec<(String, serde_json::Map<String, serde_json::Value>)> =
        serde_json::from_str(json).map_err(|e| IconParseError::Json(e.to_string()))?;
    list.into_iter().map(icon_node).collect()
}

impl IconNode {
    /// Flattens the element to subpaths in the 24-grid space; `tolerance` is
    /// the flatness bound for curves, in grid units.
    pub fn flatten(&self, tolerance: f32) -> Vec<SubPath> {
        match self {
            IconNode::Path { d } => flatten_path(d, tolerance),
            IconNode::Circle { cx, cy, r } => vec![SubPath {
                points: circle(*cx, *cy, *r, 48),
                closed: true,
            }],
            IconNode::Ellipse { cx, cy, rx, ry } => vec![SubPath {
                points: ellipse(*cx, *cy, *rx, *ry, 48),
                closed: true,
            }],
            IconNode::Rect {
                x,
                y,
                width,
                height,
                rx,
                ry,
            } => vec![SubPath {
                points: rect(*x, *y, *width, *height, *rx, *ry),
                closed: true,
            }],
            IconNode::Line { x1, y1, x2, y2 } => vec![SubPath {
                points: vec![(*x1, *y1), (*x2, *y2)],
                closed: false,
            }],
            IconNode::Polyline(points) => vec![SubPath {
                points: points.clone(),
                closed: false,
            }],
            IconNode::Polygon(points) => {
                if points.is_empty() {
                    return vec![];
                }
                let mut pts = points.clone();
                pts.push(pts[0]);
                vec![SubPath {
                    points: pts,
                    closed: true,
                }]
            }
        }
    }
}

fn icon_node(
    (kind, attrs): (String, serde_json::Map<String, serde_json::Value>),
) -> Result<IconNode, IconParseError> {
    match kind.as_str() {
        "path" => Ok(IconNode::Path {
            d: attr(&attrs, "d")
                .ok_or(IconParseError::MissingAttribute("d".to_string()))?
                .to_string(),
        }),
        "circle" => Ok(IconNode::Circle {
            cx: attr_f32(&attrs, "cx")?,
            cy: attr_f32(&attrs, "cy")?,
            r: attr_f32(&attrs, "r")?,
        }),
        "rect" => {
            let rx = attr_f32(&attrs, "rx").unwrap_or(0.0);
            let ry = match attr(&attrs, "ry") {
                Some(_) => attr_f32(&attrs, "ry")?,
                None => rx,
            };
            Ok(IconNode::Rect {
                x: attr_f32(&attrs, "x").unwrap_or(0.0),
                y: attr_f32(&attrs, "y").unwrap_or(0.0),
                width: attr_f32(&attrs, "width")?,
                height: attr_f32(&attrs, "height")?,
                rx,
                ry,
            })
        }
        "line" => Ok(IconNode::Line {
            x1: attr_f32(&attrs, "x1")?,
            y1: attr_f32(&attrs, "y1")?,
            x2: attr_f32(&attrs, "x2")?,
            y2: attr_f32(&attrs, "y2")?,
        }),
        "ellipse" => Ok(IconNode::Ellipse {
            cx: attr_f32(&attrs, "cx")?,
            cy: attr_f32(&attrs, "cy")?,
            rx: attr_f32(&attrs, "rx")?,
            ry: attr_f32(&attrs, "ry")?,
        }),
        "polyline" | "polygon" => {
            let s = attr(&attrs, "points")
                .ok_or(IconParseError::MissingAttribute("points".to_string()))?;
            let points = parse_points(s)?;
            if kind == "polyline" {
                Ok(IconNode::Polyline(points))
            } else {
                Ok(IconNode::Polygon(points))
            }
        }
        other => Err(IconParseError::UnknownElement(other.to_string())),
    }
}

fn attr<'a>(attrs: &'a serde_json::Map<String, serde_json::Value>, key: &str) -> Option<&'a str> {
    attrs.get(key).and_then(|v| v.as_str())
}

fn attr_f32(
    attrs: &serde_json::Map<String, serde_json::Value>,
    key: &str,
) -> Result<f32, IconParseError> {
    let s = attr(attrs, key).ok_or_else(|| IconParseError::MissingAttribute(key.to_string()))?;
    s.parse::<f32>()
        .map_err(|e| IconParseError::Number(key.to_string(), e.to_string()))
}

fn parse_points(s: &str) -> Result<Vec<(f32, f32)>, IconParseError> {
    let nums = parse_numbers(s).map_err(IconParseError::Path)?;
    if nums.len() % 2 != 0 {
        return Err(IconParseError::Path(format!(
            "odd number of coordinates in {s:?}"
        )));
    }
    Ok(nums.chunks_exact(2).map(|c| (c[0], c[1])).collect())
}

fn circle(cx: f32, cy: f32, r: f32, n: usize) -> Vec<(f32, f32)> {
    (0..n)
        .map(|i| {
            let a = i as f32 / n as f32 * TAU;
            (cx + r * a.cos(), cy + r * a.sin())
        })
        .collect()
}

fn ellipse(cx: f32, cy: f32, rx: f32, ry: f32, n: usize) -> Vec<(f32, f32)> {
    (0..n)
        .map(|i| {
            let a = i as f32 / n as f32 * TAU;
            (cx + rx * a.cos(), cy + ry * a.sin())
        })
        .collect()
}

fn rect(x: f32, y: f32, width: f32, height: f32, rx: f32, ry: f32) -> Vec<(f32, f32)> {
    let (x1, y1) = (x + width, y + height);
    let (rx, ry) = (rx.min(width * 0.5).max(0.0), ry.min(height * 0.5).max(0.0));
    if rx <= 0.0 || ry <= 0.0 {
        return vec![(x, y), (x1, y), (x1, y1), (x, y1)];
    }
    // The perimeter clockwise from the top-left corner, corners as little arcs.
    let corner = |cx: f32, cy: f32, from: f32, n: usize| {
        (0..=n)
            .map(|i| {
                let a = from + i as f32 / n as f32 * std::f32::consts::FRAC_PI_2;
                (cx + rx * a.cos(), cy + ry * a.sin())
            })
            .collect::<Vec<(f32, f32)>>()
    };
    let half_pi = std::f32::consts::FRAC_PI_2;
    let mut pts = Vec::with_capacity(4 * (3 + 1));
    pts.extend(corner(x1 - rx, y + ry, -half_pi, 3));
    pts.extend(corner(x1 - rx, y1 - ry, 0.0, 3));
    pts.extend(corner(x + rx, y1 - ry, half_pi, 3));
    pts.extend(corner(x + rx, y + ry, std::f32::consts::PI, 3));
    pts
}

// --- path parsing ---

fn parse_numbers(d: &str) -> Result<Vec<f32>, String> {
    let b = d.as_bytes();
    let mut out = Vec::new();
    let mut i = 0;
    while i < b.len() {
        if b[i].is_ascii_whitespace() || b[i] == b',' {
            i += 1;
            continue;
        }
        let start = i;
        if b[i] == b'+' || b[i] == b'-' {
            i += 1;
        }
        let mut digits = false;
        while i < b.len() && b[i].is_ascii_digit() {
            digits = true;
            i += 1;
        }
        if i < b.len() && b[i] == b'.' {
            i += 1;
            while i < b.len() && b[i].is_ascii_digit() {
                digits = true;
                i += 1;
            }
        }
        if digits && i < b.len() && (b[i] == b'e' || b[i] == b'E') {
            i += 1;
            if i < b.len() && (b[i] == b'+' || b[i] == b'-') {
                i += 1;
            }
            while i < b.len() && b[i].is_ascii_digit() {
                i += 1;
            }
        }
        if !digits {
            return Err(format!("malformed number at offset {start} in {d:?}"));
        }
        out.push(d[start..i].parse::<f32>().map_err(|e| e.to_string())?);
    }
    Ok(out)
}

fn flush_num(buf: &mut String, nums: &mut Vec<f32>) -> Result<(), IconParseError> {
    if !buf.is_empty() {
        nums.push(
            buf.parse::<f32>()
                .map_err(|e| IconParseError::Path(e.to_string()))?,
        );
        buf.clear();
    }
    Ok(())
}

fn tokenize(d: &str) -> Result<Vec<(char, Vec<f32>)>, IconParseError> {
    let mut out: Vec<(char, Vec<f32>)> = Vec::new();
    let mut cmd: Option<char> = None;
    let mut nums: Vec<f32> = Vec::new();
    let mut buf = String::new();
    for c in d.chars() {
        if c.is_ascii_alphabetic() {
            flush_num(&mut buf, &mut nums)?;
            if let Some(cmd) = cmd.take() {
                out.push((cmd, std::mem::take(&mut nums)));
            }
            cmd = Some(c);
        } else if c.is_ascii_whitespace() || c == ',' {
            flush_num(&mut buf, &mut nums)?;
        } else if (c == '+' || c == '-') && !buf.is_empty() && !buf.ends_with(['e', 'E']) {
            // SVG runs numbers together without a separator (`2.3-1.5`): a sign
            // after a finished number starts the next one.
            flush_num(&mut buf, &mut nums)?;
            buf.push(c);
        } else if c == '.' && buf.contains('.') {
            // A second dot ends the number (`-3.676.56` is `-3.676` `.56`).
            flush_num(&mut buf, &mut nums)?;
            buf.push(c);
        } else {
            buf.push(c);
        }
    }
    flush_num(&mut buf, &mut nums)?;
    if let Some(cmd) = cmd.take() {
        out.push((cmd, std::mem::take(&mut nums)));
    }
    Ok(out)
}

fn flatten_path(d: &str, tolerance: f32) -> Vec<SubPath> {
    let cmds = match tokenize(d) {
        Ok(c) => c,
        Err(_) => return Vec::new(),
    };
    let mut out: Vec<SubPath> = Vec::new();
    let mut points: Vec<(f32, f32)> = Vec::new();
    let mut cur = (0.0f32, 0.0f32);
    let mut start = (0.0f32, 0.0f32);
    let mut open = false;
    let mut explicit_closed = false;
    let mut last_cubic_ctrl: Option<(f32, f32)> = None;
    let mut last_quad_ctrl: Option<(f32, f32)> = None;

    for (raw, nums) in cmds {
        let abs = raw.is_ascii_uppercase();
        match raw.to_ascii_lowercase() {
            'm' => {
                finish_subpath(&mut out, &mut points, explicit_closed);
                explicit_closed = false;
                let mut it = nums.chunks_exact(2).map(|c| [c[0], c[1]]);
                if let Some([x, y]) = it.next() {
                    cur = if abs { (x, y) } else { (cur.0 + x, cur.1 + y) };
                    start = cur;
                    points.push(cur);
                    open = true;
                    for [x, y] in it {
                        cur = if abs { (x, y) } else { (cur.0 + x, cur.1 + y) };
                        points.push(cur);
                    }
                }
            }
            'z' => {
                if open && !points.is_empty() {
                    if points.last().copied() != Some(start) {
                        points.push(start);
                    }
                    explicit_closed = true;
                    finish_subpath(&mut out, &mut points, explicit_closed);
                    explicit_closed = false;
                    cur = start;
                    open = false;
                }
            }
            'l' | 'h' | 'v' | 'c' | 's' | 'q' | 't' | 'a' => {
                if !open {
                    // A drawing command after `Z` (or at the head) starts a new
                    // subpath from the current point.
                    points.push(cur);
                    open = true;
                }
                match raw.to_ascii_lowercase() {
                    'l' => {
                        for [x, y] in nums.chunks_exact(2).map(|c| [c[0], c[1]]) {
                            cur = if abs { (x, y) } else { (cur.0 + x, cur.1 + y) };
                            points.push(cur);
                        }
                    }
                    'h' => {
                        for &x in &nums {
                            cur = if abs { (x, cur.1) } else { (cur.0 + x, cur.1) };
                            points.push(cur);
                        }
                    }
                    'v' => {
                        for &y in &nums {
                            cur = if abs { (cur.0, y) } else { (cur.0, cur.1 + y) };
                            points.push(cur);
                        }
                    }
                    'c' => {
                        for [x1, y1, x2, y2, x, y] in nums
                            .chunks_exact(6)
                            .map(|c| [c[0], c[1], c[2], c[3], c[4], c[5]])
                        {
                            let p0 = cur;
                            let c1 = if abs {
                                (x1, y1)
                            } else {
                                (p0.0 + x1, p0.1 + y1)
                            };
                            let c2 = if abs {
                                (x2, y2)
                            } else {
                                (p0.0 + x2, p0.1 + y2)
                            };
                            cur = if abs { (x, y) } else { (p0.0 + x, p0.1 + y) };
                            flatten_cubic(p0, c1, c2, cur, tolerance, 0, &mut points);
                            last_cubic_ctrl = Some(c2);
                        }
                    }
                    's' => {
                        for [x2, y2, x, y] in nums.chunks_exact(4).map(|c| [c[0], c[1], c[2], c[3]])
                        {
                            let p0 = cur;
                            let c1 = reflect(last_cubic_ctrl, p0);
                            let c2 = if abs {
                                (x2, y2)
                            } else {
                                (p0.0 + x2, p0.1 + y2)
                            };
                            cur = if abs { (x, y) } else { (p0.0 + x, p0.1 + y) };
                            flatten_cubic(p0, c1, c2, cur, tolerance, 0, &mut points);
                            last_cubic_ctrl = Some(c2);
                        }
                    }
                    'q' => {
                        for [x1, y1, x, y] in nums.chunks_exact(4).map(|c| [c[0], c[1], c[2], c[3]])
                        {
                            let p0 = cur;
                            let q = if abs {
                                (x1, y1)
                            } else {
                                (p0.0 + x1, p0.1 + y1)
                            };
                            cur = if abs { (x, y) } else { (p0.0 + x, p0.1 + y) };
                            flatten_quad(p0, q, cur, tolerance, &mut points);
                            last_quad_ctrl = Some(q);
                        }
                    }
                    't' => {
                        for [x, y] in nums.chunks_exact(2).map(|c| [c[0], c[1]]) {
                            let p0 = cur;
                            let q = reflect(last_quad_ctrl, p0);
                            cur = if abs { (x, y) } else { (p0.0 + x, p0.1 + y) };
                            flatten_quad(p0, q, cur, tolerance, &mut points);
                            last_quad_ctrl = Some(q);
                        }
                    }
                    'a' => {
                        for [rx, ry, rot, large, sweep, x, y] in nums
                            .chunks_exact(7)
                            .map(|c| [c[0], c[1], c[2], c[3], c[4], c[5], c[6]])
                        {
                            let p0 = cur;
                            cur = if abs { (x, y) } else { (p0.0 + x, p0.1 + y) };
                            let arc = arc_points(p0, rx, ry, rot, large, sweep, cur);
                            points.extend(arc.into_iter().skip(1));
                        }
                    }
                    _ => unreachable!(),
                }
            }
            _ => {} // an unknown command letter: ignore the segment
        }
    }
    finish_subpath(&mut out, &mut points, explicit_closed);
    out
}

fn finish_subpath(out: &mut Vec<SubPath>, points: &mut Vec<(f32, f32)>, explicit_closed: bool) {
    if points.len() < 2 {
        points.clear();
        return;
    }
    let coincident = points.first() == points.last();
    if explicit_closed && !coincident {
        points.push(points[0]);
    }
    let closed = explicit_closed || coincident;
    out.push(SubPath {
        points: std::mem::take(points),
        closed,
    });
}

fn reflect(prev: Option<(f32, f32)>, cur: (f32, f32)) -> (f32, f32) {
    match prev {
        Some(p) => (2.0 * cur.0 - p.0, 2.0 * cur.1 - p.1),
        None => cur,
    }
}

fn flatten_quad(
    p0: (f32, f32),
    q: (f32, f32),
    p2: (f32, f32),
    tolerance: f32,
    out: &mut Vec<(f32, f32)>,
) {
    // A quadratic to its equivalent cubic, then the cubic flattener.
    let c1 = (
        p0.0 + 2.0 / 3.0 * (q.0 - p0.0),
        p0.1 + 2.0 / 3.0 * (q.1 - p0.1),
    );
    let c2 = (
        p2.0 + 2.0 / 3.0 * (q.0 - p2.0),
        p2.1 + 2.0 / 3.0 * (q.1 - p2.1),
    );
    flatten_cubic(p0, c1, c2, p2, tolerance, 0, out);
}

fn flatten_cubic(
    p0: (f32, f32),
    p1: (f32, f32),
    p2: (f32, f32),
    p3: (f32, f32),
    tolerance: f32,
    depth: u32,
    out: &mut Vec<(f32, f32)>,
) {
    if depth >= 20 {
        out.push(p3);
        return;
    }
    let flat = pt_seg_dist(p1, p0, p3).max(pt_seg_dist(p2, p0, p3));
    if flat <= tolerance {
        out.push(p3);
        return;
    }
    let m01 = mid(p0, p1);
    let m12 = mid(p1, p2);
    let m23 = mid(p2, p3);
    let m012 = mid(m01, m12);
    let m123 = mid(m12, m23);
    let m = mid(m012, m123);
    flatten_cubic(p0, m01, m012, m, tolerance, depth + 1, out);
    flatten_cubic(m, m123, m23, p3, tolerance, depth + 1, out);
}

/// Samples an SVG arc (endpoint parametrisation) to line segments. The angular
/// step is fixed small enough that the chord error stays under the flatten
/// tolerance for the largest radii Lucide uses.
fn arc_points(
    p0: (f32, f32),
    rx: f32,
    ry: f32,
    rot_deg: f32,
    large: f32,
    sweep: f32,
    to: (f32, f32),
) -> Vec<(f32, f32)> {
    if rx == 0.0 || ry == 0.0 || p0 == to {
        return vec![to];
    }
    let rot = rot_deg.to_radians();
    let (dx2, dy2) = ((p0.0 - to.0) * 0.5, (p0.1 - to.1) * 0.5);
    let (x1p, y1p) = (
        dx2 * rot.cos() + dy2 * rot.sin(),
        -dx2 * rot.sin() + dy2 * rot.cos(),
    );
    let (mut rx, mut ry) = (rx.abs(), ry.abs());
    let lam = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
    if lam > 1.0 {
        let s = lam.sqrt();
        rx *= s;
        ry *= s;
    }
    let den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
    let coef = if large != sweep { 1.0 } else { -1.0 }
        * ((rx * rx * ry * ry - den).max(0.0) / den.max(1e-12))
            .max(0.0)
            .sqrt();
    let (cxp, cyp) = (coef * rx * y1p / ry, coef * -ry * x1p / rx);
    let (cx, cy) = (
        cxp * rot.cos() - cyp * rot.sin() + (p0.0 + to.0) * 0.5,
        cxp * rot.sin() + cyp * rot.cos() + (p0.1 + to.1) * 0.5,
    );
    let (v1x, v1y) = ((x1p - cxp) / rx, (y1p - cyp) / ry);
    let (v2x, v2y) = ((-x1p - cxp) / rx, (-y1p - cyp) / ry);
    let start_angle = v1y.atan2(v1x);
    let mut delta = v2y.atan2(v2x) - start_angle;
    if sweep == 0.0 && delta > 0.0 {
        delta -= TAU;
    }
    if sweep == 1.0 && delta < 0.0 {
        delta += TAU;
    }
    let n = ((delta.abs() / 0.2).ceil() as usize).clamp(4, 256);
    (0..=n)
        .map(|i| {
            let a = start_angle + delta * i as f32 / n as f32;
            (
                cx + rx * a.cos() * rot.cos() - ry * a.sin() * rot.sin(),
                cy + rx * a.cos() * rot.sin() + ry * a.sin() * rot.cos(),
            )
        })
        .collect()
}

fn mid(a: (f32, f32), b: (f32, f32)) -> (f32, f32) {
    ((a.0 + b.0) * 0.5, (a.1 + b.1) * 0.5)
}

fn len(a: (f32, f32), b: (f32, f32)) -> f32 {
    ((a.0 - b.0).powi(2) + (a.1 - b.1).powi(2)).sqrt()
}

fn pt_seg_dist(p: (f32, f32), a: (f32, f32), b: (f32, f32)) -> f32 {
    let (dx, dy) = (b.0 - a.0, b.1 - a.1);
    let len2 = dx * dx + dy * dy;
    if len2 < 1e-12 {
        return len(p, a);
    }
    let t = ((p.0 - a.0) * dx + (p.1 - a.1) * dy) / len2;
    let (px, py) = (a.0 + t * dx, a.1 + t * dy);
    len(p, (px, py))
}

// --- scene building ---

fn map_grid(p: (f32, f32)) -> (f32, f32) {
    (0.1 + p.0 / 24.0 * 0.8, 0.1 + p.1 / 24.0 * 0.8)
}

fn to_points(points: &[(f32, f32)], frame: &Frame) -> Vec<Point> {
    points.iter().map(|&(x, y)| frame.pt(x, y)).collect()
}

/// The marks laid for `detail`: everything at `Medium` and up, at `Small` only
/// the longest few that survive a length floor, since a 48 px icon cannot
/// carry every stroke. `max_marks` is the per-icon `Small` cap.
fn select_marks(marks: &[SubPath], small: bool, max_marks: usize) -> Vec<SubPath> {
    if !small {
        return marks.to_vec();
    }
    let mut kept: Vec<SubPath> = marks
        .iter()
        .filter(|m| m.length() >= SMALL_MIN_MARK_LENGTH)
        .cloned()
        .collect();
    kept.sort_by(|a, b| b.length().total_cmp(&a.length()));
    kept.truncate(max_marks);
    kept
}

/// The hand-authored mapping: closed subpaths are stencil-and-fill bodies in
/// `BaseWash`, open ones wet-on-dry marks in `Shadow` at the Lucide stroke
/// radius. An icon with no closed subpath lays its longest open one as a fat
/// `BaseWash` body instead. `hints` tunes all of that per icon.
pub(crate) fn svg_icon(
    style: &Style,
    palette: &Palette,
    name: &str,
    elements: &[IconNode],
    hints: &IconHints,
) -> Scene {
    let frame = Frame::new(SQUARE);
    let body_pigment = role(palette, hints.body_role);
    let mark_pigment = role(palette, hints.mark_role);
    let mark_radius = 0.8 / 24.0 * hints.mark_radius;
    let mut subpaths: Vec<SubPath> = elements
        .iter()
        .flat_map(|e| e.flatten(FLATTEN_TOLERANCE))
        .collect();
    for s in &mut subpaths {
        for p in &mut s.points {
            *p = map_grid(*p);
        }
    }
    let mut bodies: Vec<SubPath> = Vec::new();
    let mut marks: Vec<SubPath> = Vec::new();
    for (i, sub) in subpaths.into_iter().enumerate() {
        if sub.closed || hints.fill.contains(&i) {
            let mut body = sub;
            if !body.closed {
                body.points.push(body.points[0]);
                body.closed = true;
            }
            bodies.push(body);
        } else {
            marks.push(sub);
        }
    }

    let mut p = Painting::new(style.ticks(400));
    if !bodies.is_empty() {
        for (i, body) in bodies.iter().enumerate() {
            let at = 0.06 + 0.46 * i as f32 / bodies.len() as f32;
            stencil_body(
                &mut p,
                &frame,
                style,
                &Shape(body.points.clone()),
                0.012,
                body_pigment,
                0.44,
                at,
            );
            for &(cx, cy, r) in &hints.holes {
                let (x, y) = map_grid((cx, cy));
                p.at(
                    at + 0.02,
                    lift(vec![frame.pt(x, y)], r * 0.8 / 24.0, 1.0, 0.3),
                );
            }
            p.glaze(at + 0.06, 0.1).clear_mask(at + 0.06);
        }
    } else if let Some(longest) = marks
        .iter()
        .max_by(|a, b| a.length().total_cmp(&b.length()))
        .cloned()
    {
        p.at(
            0.06,
            brush(
                to_points(&longest.points, &frame),
                mark_radius * BODY_STROKE_FACTOR,
                body_pigment,
                style.conc(0.5),
                style.water(0.55),
                0.5,
            ),
        );
        p.glaze(0.5, 0.1);
        marks.retain(|s| s != &longest);
    }
    let marks = select_marks(&marks, !style.fine(), hints.small_marks);
    for (i, mark) in marks.iter().enumerate() {
        let at = 0.62 + 0.2 * i as f32 / marks.len().max(1) as f32;
        p.at(
            at,
            brush(
                to_points(&mark.points, &frame),
                mark_radius,
                mark_pigment,
                style.conc(0.8),
                style.water(0.3),
                0.45,
            ),
        );
    }
    p.settle(0.88, 3.0);
    style.scene(
        &format!("lucide-{name}"),
        palette,
        SQUARE,
        Paper::cold_press(style.seed()),
        p.finish(),
    )
}

/// Convenience wrapper over [`svg_icon`] for callers outside the crate: builds
/// the [`Style`] from the catalogue arguments and choreographs the scene.
#[allow(clippy::too_many_arguments)]
pub fn svg_icon_scene(
    name: &str,
    elements: &[IconNode],
    seed: Seed,
    palette: &Palette,
    intensity: f32,
    detail: DetailLevel,
    background: Background,
    sim_resolution: Option<u32>,
    hints: &IconHints,
) -> Scene {
    let style = style_for(seed, intensity, detail, background, sim_resolution);
    let mut scene = svg_icon(&style, palette, name, elements, hints);
    choreograph_scene(&mut scene, &style.choreography());
    scene
}

#[cfg(test)]
mod tests {
    use super::*;
    use nocturne_watercolour_core::domain::Operation;

    #[test]
    fn tokenizes_mixed_absolute_and_relative() {
        let cmds = tokenize("M12 6v6l4 2").unwrap();
        assert_eq!(
            cmds,
            vec![
                ('M', vec![12.0, 6.0]),
                ('v', vec![6.0]),
                ('l', vec![4.0, 2.0])
            ]
        );
    }

    #[test]
    fn flattens_the_clock_hand() {
        let s = flatten_path("M12 6v6l4 2", 0.25);
        assert_eq!(s.len(), 1);
        assert!(!s[0].closed);
        let p = &s[0].points;
        assert_eq!(p[0], (12.0, 6.0));
        assert_eq!(p[1], (12.0, 12.0));
        assert_eq!(p[2], (16.0, 14.0));
    }

    #[test]
    fn flattens_a_relative_arc() {
        let s = flatten_path("M4 9a5 5 0 0 1 8 4", 0.25);
        assert_eq!(s.len(), 1);
        let p = &s[0].points;
        assert_eq!(p[0], (4.0, 9.0));
        assert!(p.len() >= 4, "arc is flattened, not a chord");
        assert!(!s[0].closed);
        let last = p.last().unwrap();
        assert!((last.0 - 12.0).abs() < 1e-3 && (last.1 - 13.0).abs() < 1e-3);
    }

    #[test]
    fn flattens_s_and_t_chains() {
        let s = flatten_path("M0 0C4 0 4 4 8 4S12 8 16 8", 0.25);
        assert_eq!(s[0].points[0], (0.0, 0.0));
        assert_eq!(*s[0].points.last().unwrap(), (16.0, 8.0));
        assert!(s[0].points.len() > 4, "smooth chain is curved");
        let q = flatten_path("M0 8Q4 4 8 8T16 8", 0.25);
        assert_eq!(*q[0].points.last().unwrap(), (16.0, 8.0));
        assert!(q[0].points.len() > 4, "quadratic chain is curved");
    }

    #[test]
    fn an_implicit_close_subpath_closes() {
        let s = flatten_path("M2 9.5L22 9.5c0 2.3-1.5 4-3 5.5L2 9.5", 0.25);
        assert!(s[0].closed);
        assert_eq!(s[0].points.first(), s[0].points.last());
    }

    #[test]
    fn z_closes_explicitly_and_splits_subpaths() {
        let s = flatten_path("M0 0L10 0L10 10Z", 0.25);
        assert_eq!(s.len(), 1);
        assert!(s[0].closed);
        assert_eq!(s[0].points.first(), s[0].points.last());
        // A `z` in the middle ends one subpath; the commands after start another.
        let two = flatten_path("M19 8l3 8a5 5 0 0 1-6 0zV7", 0.25);
        assert_eq!(two.len(), 2);
        assert!(two[0].closed);
        assert!(!two[1].closed);
    }

    #[test]
    fn moves_split_subpaths() {
        let s = flatten_path("M0 0L1 0M5 5L6 5", 0.25);
        assert_eq!(s.len(), 2);
        assert_eq!(s[1].points[0], (5.0, 5.0));
    }

    #[test]
    fn parses_every_element_kind() {
        let json = r#"[["circle",{"cx":"12","cy":"12","r":"10"}],["rect",{"x":"2","y":"4","width":"18","height":"18","rx":"2"}],["line",{"x1":"6","y1":"6","x2":"6.01","y2":"6"}],["ellipse",{"cx":"12","cy":"5","rx":"9","ry":"3"}],["polyline",{"points":"0,0 24,0 24,24"}],["polygon",{"points":"0,0 24,0 24,24"}],["path",{"d":"M12 6v6l4 2"}]]"#;
        let nodes = parse_icon_elements(json).unwrap();
        assert_eq!(nodes.len(), 7);
        assert!(matches!(
            &nodes[0],
            IconNode::Circle {
                cx: 12.0,
                r: 10.0,
                ..
            }
        ));
        assert!(matches!(
            &nodes[1],
            IconNode::Rect {
                width: 18.0,
                rx: 2.0,
                ..
            }
        ));
        assert!(matches!(&nodes[2], IconNode::Line { x1: 6.0, .. }));
        assert!(matches!(&nodes[3], IconNode::Ellipse { ry: 3.0, .. }));
        assert_eq!(nodes[4].flatten(0.25).len(), 1);
        assert!(nodes[5].flatten(0.25)[0].closed);
        assert_eq!(nodes[6].flatten(0.25)[0].points.len(), 3);
    }

    #[test]
    fn parse_errors_are_typed() {
        assert!(matches!(
            parse_icon_elements("[["),
            Err(IconParseError::Json(_))
        ));
        assert!(matches!(
            parse_icon_elements(r#"[["path",{}]]"#),
            Err(IconParseError::MissingAttribute(d)) if d == "d"
        ));
        assert!(matches!(
            parse_icon_elements(r#"[["blob",{}]]"#),
            Err(IconParseError::UnknownElement(_))
        ));
    }

    #[test]
    fn marks_are_gated_at_small() {
        let short = SubPath {
            points: vec![(0.2, 0.2), (0.22, 0.2)],
            closed: false,
        };
        let long = SubPath {
            points: vec![(0.2, 0.2), (0.6, 0.2)],
            closed: false,
        };
        assert_eq!(
            select_marks(&[long.clone(), short.clone()], false, 4).len(),
            2
        );
        let small = select_marks(&[long.clone(), short.clone()], true, 4);
        assert_eq!(small.len(), 1);
        assert_eq!(small[0], long);
    }

    #[test]
    fn hints_json_round_trips_through_camel_case() {
        let doc = IconHintsDoc {
            fill: vec![1, 3],
            mark_radius: Some(1.4),
            small_marks: Some(3),
            holes: vec![[7.5, 15.5, 2.2]],
            body_role: Some("accent".into()),
            mark_role: Some("glow".into()),
        };
        let json = serde_json::to_string(&doc).unwrap();
        assert!(json.contains("\"markRadius\""));
        assert!(json.contains("\"smallMarks\""));
        let hints = parse_icon_hints(&json).unwrap();
        assert_eq!(hints.fill, vec![1, 3]);
        assert_eq!(hints.mark_radius, 1.4);
        assert_eq!(hints.small_marks, 3);
        assert_eq!(hints.holes, vec![(7.5, 15.5, 2.2)]);
        assert_eq!(hints.body_role, PigmentRole::Accent);
        assert_eq!(hints.mark_role, PigmentRole::Glow);
        let back: IconHintsDoc = serde_json::from_str(&json).unwrap();
        assert_eq!(back, doc);
        assert!(matches!(
            parse_icon_hints(r#"{"markRole":"neon"}"#),
            Err(IconParseError::Role(role)) if role == "neon"
        ));
    }

    #[test]
    fn empty_hints_json_yields_the_default() {
        assert_eq!(parse_icon_hints("").unwrap(), IconHints::default());
        assert_eq!(parse_icon_hints("{}").unwrap(), IconHints::default());
    }

    #[test]
    fn fill_closes_named_open_subpaths_into_bodies() {
        let json = r#"[["ellipse",{"cx":"12","cy":"5","rx":"9","ry":"3"}],["path",{"d":"M3 5V19A9 3 0 0 0 21 19V5"}],["path",{"d":"M3 12A9 3 0 0 0 21 12"}]]"#;
        let elements = parse_icon_elements(json).unwrap();
        let palette = Palette::moonlight();
        let style = Style::new(Seed(7), 0.7, DetailLevel::Large);
        let plain = svg_icon(
            &style,
            &palette,
            "database",
            &elements,
            &IconHints::default(),
        );
        let hinted = svg_icon(
            &style,
            &palette,
            "database",
            &elements,
            &IconHints {
                fill: vec![1],
                ..Default::default()
            },
        );
        let masks = |s: &Scene| {
            s.timeline
                .events
                .iter()
                .filter(|e| matches!(&e.op, Operation::SetMask(_)))
                .count()
        };
        assert_eq!(masks(&plain), 1, "ellipse only");
        assert_eq!(masks(&hinted), 2, "ellipse plus closed sides");
    }

    #[test]
    fn holes_lift_between_hatch_and_glaze() {
        let json = r#"[["circle",{"cx":"7.5","cy":"15.5","r":"5.5"}]]"#;
        let elements = parse_icon_elements(json).unwrap();
        let palette = Palette::moonlight();
        let style = Style::new(Seed(7), 0.7, DetailLevel::Large);
        let scene = svg_icon(
            &style,
            &palette,
            "key",
            &elements,
            &IconHints {
                holes: vec![(7.5, 15.5, 2.2)],
                ..Default::default()
            },
        );
        let events = &scene.timeline.events;
        let tick_of = |op: fn(&Operation) -> bool| {
            events
                .iter()
                .find(|e| op(&e.op))
                .map(|e| e.at_tick)
                .unwrap()
        };
        let mask_tick = tick_of(|op| matches!(op, Operation::SetMask(_)));
        let lift_tick = tick_of(|op| matches!(op, Operation::Lift(_)));
        let glaze_end = events
            .iter()
            .filter(|e| matches!(&e.op, Operation::Settle { .. }))
            .map(|e| e.at_tick)
            .max()
            .unwrap();
        assert!(
            mask_tick < lift_tick && lift_tick < glaze_end,
            "lift between hatch (tick {mask_tick}) and glaze end (tick {glaze_end}), was {lift_tick}"
        );
    }

    #[test]
    fn every_test_set_icon_builds_a_valid_scene() {
        let json = std::fs::read_to_string(concat!(
            env!("CARGO_MANIFEST_DIR"),
            "/tests/fixtures/lucide-icons.json"
        ))
        .expect("tests/fixtures/lucide-icons.json");
        let data: serde_json::Map<String, serde_json::Value> =
            serde_json::from_str(&json).expect("icon map");
        let palette = Palette::moonlight();
        for (name, elements) in &data {
            let elements = parse_icon_elements(&elements.to_string()).unwrap_or_else(|e| {
                panic!("{name}: {e}");
            });
            for detail in DetailLevel::ALL {
                let scene = svg_icon_scene(
                    name,
                    &elements,
                    Seed(7),
                    &palette,
                    0.7,
                    detail,
                    Background::Transparent,
                    None,
                    &IconHints::default(),
                );
                assert_eq!(scene.validate(), Ok(()), "{name} / {detail:?}");
                assert!(
                    scene.id.0.starts_with("lucide-"),
                    "{name} id: {}",
                    scene.id.0
                );
            }
        }
    }
}
