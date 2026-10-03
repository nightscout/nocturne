//! Versioned serde documents for scenes, mapped explicitly to and from the
//! serde-free domain types.

use nocturne_watercolour_core::domain::{
    Background, BrushStroke, Dab, LiftStroke, Mask, Operation, Palette, PaletteEntry, Paper,
    Pigment, PigmentRole, Point, RadiusProfile, Rgb, Scene, SceneId, Seed, SimResolution, SizeHint,
    StrokeSpan, Timeline, TimelineEvent, ValidationError, WaterStroke,
};
use serde::{Deserialize, Serialize};

pub const CURRENT_VERSION: u32 = 1;

#[derive(Debug)]
pub enum DocumentError {
    Json(serde_json::Error),
    UnsupportedVersion { found: u32, supported: u32 },
    MissingVersion,
    UnknownRole(String),
    UnknownBackground(String),
    Invalid(Vec<ValidationError>),
}

impl std::fmt::Display for DocumentError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            DocumentError::Json(e) => write!(f, "json: {e}"),
            DocumentError::UnsupportedVersion { found, supported } => {
                write!(
                    f,
                    "document version {found} not supported (this build reads {supported})"
                )
            }
            DocumentError::MissingVersion => f.write_str("document has no version field"),
            DocumentError::UnknownRole(r) => write!(f, "unknown pigment role {r:?}"),
            DocumentError::UnknownBackground(b) => write!(f, "unknown background {b:?}"),
            DocumentError::Invalid(errs) => write!(f, "scene invalid: {errs:?}"),
        }
    }
}

impl std::error::Error for DocumentError {}

impl From<serde_json::Error> for DocumentError {
    fn from(e: serde_json::Error) -> Self {
        DocumentError::Json(e)
    }
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct SceneDocumentV1 {
    pub version: u32,
    pub id: String,
    pub size_hint: [u32; 2],
    pub seed: u64,
    pub sim_resolution: u32,
    /// `"transparent"` (subtractive compositing, light hosts) or
    /// `"transparent_on_dark"` (luminous compositing). Absent in documents
    /// written before the field existed, which read as `"transparent"`.
    #[serde(default = "default_background")]
    pub background: String,
    pub paper: PaperDoc,
    pub palette: PaletteDoc,
    pub timeline: TimelineDoc,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PaperDoc {
    pub seed: u64,
    pub grain_scale: f32,
    pub height_amplitude: f32,
    pub absorbency: [f32; 2],
    pub fibre_anisotropy: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PaletteDoc {
    pub name: String,
    pub entries: Vec<PaletteEntryDoc>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PaletteEntryDoc {
    pub role: String,
    pub pigment: PigmentDoc,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct PigmentDoc {
    pub name: String,
    pub k: [f32; 3],
    pub s: [f32; 3],
    pub density: f32,
    pub staining_power: f32,
    pub granulation: f32,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct TimelineDoc {
    pub total_ticks: u32,
    pub events: Vec<EventDoc>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct EventDoc {
    pub at_tick: u32,
    pub op: OperationDoc,
}

/// Externally tagged (`{"brush": {...}}`), not `tag = "kind"`: internally
/// tagged enums buffer their content through `serde::__private::Content`,
/// and with `serde_json`'s `arbitrary_precision` feature (enabled elsewhere
/// in this workspace) buffered numbers come back as maps and fail to parse
/// as `f32`.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum OperationDoc {
    Dab {
        center: [f32; 2],
        radius: f32,
        pigment: usize,
        concentration: f32,
        water: f32,
        softness: f32,
    },
    Brush {
        path: Vec<[f32; 2]>,
        radius: [f32; 2],
        pigment: usize,
        concentration: f32,
        water: f32,
        softness: f32,
        #[serde(default = "full_span")]
        span: [f32; 2],
    },
    Water {
        path: Vec<[f32; 2]>,
        radius: [f32; 2],
        water: f32,
        softness: f32,
        #[serde(default = "full_span")]
        span: [f32; 2],
    },
    Lift {
        path: Vec<[f32; 2]>,
        radius: [f32; 2],
        strength: f32,
        softness: f32,
        #[serde(default = "full_span")]
        span: [f32; 2],
    },
    Dry {
        rate: f32,
    },
    Settle {
        share: f32,
    },
    DryAll,
    SetMask {
        mask: MaskDoc,
    },
    ClearMask,
}

/// Externally tagged for the reason given on [`OperationDoc`].
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum MaskDoc {
    Polygon {
        points: Vec<[f32; 2]>,
        feather: f32,
    },
    Path {
        points: Vec<[f32; 2]>,
        radius: f32,
        feather: f32,
    },
}

fn default_background() -> String {
    "transparent".to_string()
}

fn full_span() -> [f32; 2] {
    [0.0, 1.0]
}

fn background_name(background: Background) -> &'static str {
    match background {
        Background::Transparent => "transparent",
        Background::TransparentOnDark => "transparent_on_dark",
    }
}

fn parse_background(name: &str) -> Result<Background, DocumentError> {
    Ok(match name {
        "transparent" => Background::Transparent,
        "transparent_on_dark" => Background::TransparentOnDark,
        other => return Err(DocumentError::UnknownBackground(other.to_string())),
    })
}

fn role_name(role: PigmentRole) -> &'static str {
    match role {
        PigmentRole::BaseWash => "base_wash",
        PigmentRole::Shadow => "shadow",
        PigmentRole::Accent => "accent",
        PigmentRole::Glow => "glow",
    }
}

fn parse_role(name: &str) -> Result<PigmentRole, DocumentError> {
    Ok(match name {
        "base_wash" => PigmentRole::BaseWash,
        "shadow" => PigmentRole::Shadow,
        "accent" => PigmentRole::Accent,
        "glow" => PigmentRole::Glow,
        other => return Err(DocumentError::UnknownRole(other.to_string())),
    })
}

fn points_to_doc(points: &[Point]) -> Vec<[f32; 2]> {
    points.iter().map(|p| [p.x, p.y]).collect()
}

fn points_from_doc(points: &[[f32; 2]]) -> Vec<Point> {
    points.iter().map(|p| Point::new(p[0], p[1])).collect()
}

fn radius_to_doc(r: RadiusProfile) -> [f32; 2] {
    [r.start, r.end]
}

fn radius_from_doc(r: [f32; 2]) -> RadiusProfile {
    RadiusProfile {
        start: r[0],
        end: r[1],
    }
}

fn mask_to_doc(mask: &Mask) -> MaskDoc {
    match mask {
        Mask::Polygon { points, feather } => MaskDoc::Polygon {
            points: points_to_doc(points),
            feather: *feather,
        },
        Mask::Path {
            points,
            radius,
            feather,
        } => MaskDoc::Path {
            points: points_to_doc(points),
            radius: *radius,
            feather: *feather,
        },
    }
}

fn mask_from_doc(mask: MaskDoc) -> Mask {
    match mask {
        MaskDoc::Polygon { points, feather } => Mask::Polygon {
            points: points_from_doc(&points),
            feather,
        },
        MaskDoc::Path {
            points,
            radius,
            feather,
        } => Mask::Path {
            points: points_from_doc(&points),
            radius,
            feather,
        },
    }
}

fn span_to_doc(span: StrokeSpan) -> [f32; 2] {
    [span.start, span.end]
}

fn span_from_doc(span: [f32; 2]) -> StrokeSpan {
    StrokeSpan::new(span[0], span[1])
}

fn op_to_doc(op: &Operation) -> OperationDoc {
    match op {
        Operation::Dab(s) => OperationDoc::Dab {
            center: [s.center.x, s.center.y],
            radius: s.radius,
            pigment: s.pigment,
            concentration: s.concentration,
            water: s.water,
            softness: s.softness,
        },
        Operation::Brush(s) => OperationDoc::Brush {
            path: points_to_doc(&s.path),
            radius: radius_to_doc(s.radius),
            pigment: s.pigment,
            concentration: s.concentration,
            water: s.water,
            softness: s.softness,
            span: span_to_doc(s.span),
        },
        Operation::Water(s) => OperationDoc::Water {
            path: points_to_doc(&s.path),
            radius: radius_to_doc(s.radius),
            water: s.water,
            softness: s.softness,
            span: span_to_doc(s.span),
        },
        Operation::Lift(s) => OperationDoc::Lift {
            path: points_to_doc(&s.path),
            radius: radius_to_doc(s.radius),
            strength: s.strength,
            softness: s.softness,
            span: span_to_doc(s.span),
        },
        Operation::Dry { rate } => OperationDoc::Dry { rate: *rate },
        Operation::Settle { share } => OperationDoc::Settle { share: *share },
        Operation::DryAll => OperationDoc::DryAll,
        Operation::SetMask(m) => OperationDoc::SetMask {
            mask: mask_to_doc(m),
        },
        Operation::ClearMask => OperationDoc::ClearMask,
    }
}

fn op_from_doc(op: OperationDoc) -> Operation {
    match op {
        OperationDoc::Dab {
            center,
            radius,
            pigment,
            concentration,
            water,
            softness,
        } => Operation::Dab(Dab {
            center: Point::new(center[0], center[1]),
            radius,
            pigment,
            concentration,
            water,
            softness,
        }),
        OperationDoc::Brush {
            path,
            radius,
            pigment,
            concentration,
            water,
            softness,
            span,
        } => Operation::Brush(BrushStroke {
            path: points_from_doc(&path),
            radius: radius_from_doc(radius),
            pigment,
            concentration,
            water,
            softness,
            span: span_from_doc(span),
        }),
        OperationDoc::Water {
            path,
            radius,
            water,
            softness,
            span,
        } => Operation::Water(WaterStroke {
            path: points_from_doc(&path),
            radius: radius_from_doc(radius),
            water,
            softness,
            span: span_from_doc(span),
        }),
        OperationDoc::Lift {
            path,
            radius,
            strength,
            softness,
            span,
        } => Operation::Lift(LiftStroke {
            path: points_from_doc(&path),
            radius: radius_from_doc(radius),
            strength,
            softness,
            span: span_from_doc(span),
        }),
        OperationDoc::Dry { rate } => Operation::Dry { rate },
        OperationDoc::Settle { share } => Operation::Settle { share },
        OperationDoc::DryAll => Operation::DryAll,
        OperationDoc::SetMask { mask } => Operation::SetMask(mask_from_doc(mask)),
        OperationDoc::ClearMask => Operation::ClearMask,
    }
}

pub fn to_document(scene: &Scene) -> SceneDocumentV1 {
    SceneDocumentV1 {
        version: CURRENT_VERSION,
        id: scene.id.0.clone(),
        size_hint: [scene.size_hint.width, scene.size_hint.height],
        seed: scene.seed.0,
        sim_resolution: scene.sim_resolution.0,
        background: background_name(scene.background).to_string(),
        paper: PaperDoc {
            seed: scene.paper.seed.0,
            grain_scale: scene.paper.grain_scale,
            height_amplitude: scene.paper.height_amplitude,
            absorbency: scene.paper.absorbency,
            fibre_anisotropy: scene.paper.fibre_anisotropy,
        },
        palette: PaletteDoc {
            name: scene.palette.name.clone(),
            entries: scene
                .palette
                .entries
                .iter()
                .map(|e| PaletteEntryDoc {
                    role: role_name(e.role).to_string(),
                    pigment: PigmentDoc {
                        name: e.pigment.name.clone(),
                        k: e.pigment.k.0,
                        s: e.pigment.s.0,
                        density: e.pigment.density,
                        staining_power: e.pigment.staining_power,
                        granulation: e.pigment.granulation,
                    },
                })
                .collect(),
        },
        timeline: TimelineDoc {
            total_ticks: scene.timeline.total_ticks,
            events: scene
                .timeline
                .events
                .iter()
                .map(|e| EventDoc {
                    at_tick: e.at_tick,
                    op: op_to_doc(&e.op),
                })
                .collect(),
        },
    }
}

/// Maps and validates.
pub fn from_document(doc: SceneDocumentV1) -> Result<Scene, DocumentError> {
    if doc.version != CURRENT_VERSION {
        return Err(DocumentError::UnsupportedVersion {
            found: doc.version,
            supported: CURRENT_VERSION,
        });
    }
    let mut entries = Vec::with_capacity(doc.palette.entries.len());
    for e in doc.palette.entries {
        entries.push(PaletteEntry {
            role: parse_role(&e.role)?,
            pigment: Pigment {
                name: e.pigment.name,
                k: Rgb(e.pigment.k),
                s: Rgb(e.pigment.s),
                density: e.pigment.density,
                staining_power: e.pigment.staining_power,
                granulation: e.pigment.granulation,
            },
        });
    }
    let background = parse_background(&doc.background)?;
    let scene = Scene {
        id: SceneId(doc.id),
        size_hint: SizeHint {
            width: doc.size_hint[0],
            height: doc.size_hint[1],
        },
        paper: Paper {
            seed: Seed(doc.paper.seed),
            grain_scale: doc.paper.grain_scale,
            height_amplitude: doc.paper.height_amplitude,
            absorbency: doc.paper.absorbency,
            fibre_anisotropy: doc.paper.fibre_anisotropy,
        },
        palette: Palette::new(doc.palette.name, entries),
        timeline: Timeline {
            events: doc
                .timeline
                .events
                .into_iter()
                .map(|e| TimelineEvent {
                    at_tick: e.at_tick,
                    op: op_from_doc(e.op),
                })
                .collect(),
            total_ticks: doc.timeline.total_ticks,
        },
        seed: Seed(doc.seed),
        sim_resolution: SimResolution(doc.sim_resolution),
        background,
    };
    scene.validate().map_err(DocumentError::Invalid)?;
    Ok(scene)
}

pub fn scene_to_json(scene: &Scene) -> Result<String, DocumentError> {
    Ok(serde_json::to_string_pretty(&to_document(scene))?)
}

/// Parses the body once; only when that fails is `version` read on its own,
/// so a newer document fails with `UnsupportedVersion` (or a document
/// without one with `MissingVersion`) rather than a field error deep in the
/// body.
pub fn parse_scene_json(json: &str) -> Result<Scene, DocumentError> {
    #[derive(Deserialize)]
    struct VersionOnly {
        version: Option<u32>,
    }
    let body_error = match serde_json::from_str::<SceneDocumentV1>(json) {
        Ok(doc) => return from_document(doc),
        Err(e) => e,
    };
    let head: VersionOnly = serde_json::from_str(json)?;
    let version = head.version.ok_or(DocumentError::MissingVersion)?;
    if version != CURRENT_VERSION {
        return Err(DocumentError::UnsupportedVersion {
            found: version,
            supported: CURRENT_VERSION,
        });
    }
    Err(body_error.into())
}

/// An operation appended to a live session, `after_ticks` from its next tick.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct AppendedEventDoc {
    pub after_ticks: u32,
    pub op: OperationDoc,
}

/// Parses a JSON array of [`AppendedEventDoc`]. Validating the operations
/// needs the scene they join, so it is left to the session.
pub fn parse_appended_json(json: &str) -> Result<Vec<(u32, Operation)>, DocumentError> {
    let docs: Vec<AppendedEventDoc> = serde_json::from_str(json)?;
    Ok(docs
        .into_iter()
        .map(|d| (d.after_ticks, op_from_doc(d.op)))
        .collect())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::authoring::ArtworkCatalogue;

    #[test]
    fn parses_appended_operations_with_their_offsets() {
        let ops = parse_appended_json(
            r#"[{"after_ticks": 0, "op": "clear_mask"}, {"after_ticks": 4, "op": {"dry": {"rate": 2.0}}}]"#,
        )
        .unwrap();
        assert_eq!(
            ops,
            vec![(0, Operation::ClearMask), (4, Operation::Dry { rate: 2.0 })]
        );
        assert!(parse_appended_json(r#"[{"op": "clear_mask"}]"#).is_err());
    }

    #[test]
    fn round_trips_every_catalogue_scene() {
        for entry in ArtworkCatalogue::entries() {
            let scene = (entry.build)(Seed(9), Palette::dusk());
            let json = scene_to_json(&scene).unwrap();
            let back = parse_scene_json(&json).unwrap();
            assert_eq!(back, scene, "{}", entry.name);
            assert_eq!(to_document(&back), to_document(&scene));
        }
    }

    #[test]
    fn rejects_newer_and_missing_versions_before_reading_the_body() {
        let err = parse_scene_json(r#"{"version": 2, "garbage": true}"#).unwrap_err();
        assert!(matches!(
            err,
            DocumentError::UnsupportedVersion {
                found: 2,
                supported: 1
            }
        ));
        let err = parse_scene_json(r#"{"id": "x"}"#).unwrap_err();
        assert!(matches!(err, DocumentError::MissingVersion));
    }

    #[test]
    fn background_round_trips_and_defaults_when_absent() {
        let mut scene = ArtworkCatalogue::build("wash", Seed(2), Palette::water()).unwrap();
        scene.background = Background::TransparentOnDark;
        let json = scene_to_json(&scene).unwrap();
        assert!(json.contains("\"transparent_on_dark\""));
        let back = parse_scene_json(&json).unwrap();
        assert_eq!(back.background, Background::TransparentOnDark);

        let mut value: serde_json::Value = serde_json::from_str(&json).unwrap();
        value.as_object_mut().unwrap().remove("background");
        let legacy = parse_scene_json(&value.to_string()).unwrap();
        assert_eq!(legacy.background, Background::Transparent);

        value["background"] = serde_json::Value::String("plaid".into());
        assert!(matches!(
            parse_scene_json(&value.to_string()),
            Err(DocumentError::UnknownBackground(_))
        ));
    }

    #[test]
    fn brush_op_without_span_round_trips_to_full_span() {
        let json = r#"{"brush":{"path":[[0.2,0.5],[0.8,0.5]],"radius":[0.1,0.1],"pigment":0,"concentration":0.5,"water":0.8,"softness":0.3}}"#;
        let doc: OperationDoc = serde_json::from_str(json).unwrap();
        let op = op_from_doc(doc);
        let Operation::Brush(s) = op else {
            panic!("expected a brush op");
        };
        assert_eq!(s.span, StrokeSpan::FULL);
    }

    #[test]
    fn rejects_invalid_scene_content() {
        let scene = ArtworkCatalogue::build("wash", Seed(1), Palette::moss()).unwrap();
        let mut doc = to_document(&scene);
        doc.sim_resolution = 4096;
        assert!(matches!(from_document(doc), Err(DocumentError::Invalid(_))));
        let mut doc = to_document(&scene);
        doc.palette.entries[0].role = "sparkle".into();
        assert!(matches!(
            from_document(doc),
            Err(DocumentError::UnknownRole(_))
        ));
    }
}
