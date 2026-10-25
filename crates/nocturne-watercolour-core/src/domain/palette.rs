//! Ordered pigment sets with roles the timeline builder can address.

use super::pigment::{Pigment, builtin};

#[derive(Debug, Clone, Copy, PartialEq, Eq, Hash)]
pub enum PigmentRole {
    BaseWash,
    Shadow,
    Accent,
    Glow,
}

#[derive(Debug, Clone, PartialEq)]
pub struct PaletteEntry {
    pub pigment: Pigment,
    pub role: PigmentRole,
}

/// Upper bound on pigments per scene; the simulation grid and the shader
/// port size their per-pigment storage from it.
pub const MAX_PIGMENTS: usize = 8;

#[derive(Debug, Clone, PartialEq)]
pub struct Palette {
    pub name: String,
    pub entries: Vec<PaletteEntry>,
}

impl Palette {
    pub const NAMES: [&'static str; 6] = ["moonlight", "water", "dusk", "ember", "moss", "slate"];

    pub fn new(name: impl Into<String>, entries: Vec<PaletteEntry>) -> Self {
        Palette {
            name: name.into(),
            entries,
        }
    }

    pub fn len(&self) -> usize {
        self.entries.len()
    }

    pub fn is_empty(&self) -> bool {
        self.entries.is_empty()
    }

    /// Index of the first pigment with `role`, if any.
    pub fn index_of(&self, role: PigmentRole) -> Option<usize> {
        self.entries.iter().position(|e| e.role == role)
    }

    pub fn pigment(&self, index: usize) -> Option<&Pigment> {
        self.entries.get(index).map(|e| &e.pigment)
    }

    pub fn pigments(&self) -> impl Iterator<Item = &Pigment> {
        self.entries.iter().map(|e| &e.pigment)
    }

    pub fn moonlight() -> Palette {
        Self::named(
            "moonlight",
            [
                (builtin::indigo(), PigmentRole::BaseWash),
                (builtin::paynes_grey(), PigmentRole::Shadow),
                (builtin::quinacridone_rose(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    pub fn water() -> Palette {
        Self::named(
            "water",
            [
                (builtin::cerulean(), PigmentRole::BaseWash),
                (builtin::phthalo_blue(), PigmentRole::Shadow),
                (builtin::viridian(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    pub fn dusk() -> Palette {
        Self::named(
            "dusk",
            [
                (builtin::quinacridone_rose(), PigmentRole::BaseWash),
                (builtin::indigo(), PigmentRole::Shadow),
                (builtin::ember_orange(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    pub fn ember() -> Palette {
        Self::named(
            "ember",
            [
                (builtin::ember_orange(), PigmentRole::BaseWash),
                (builtin::burnt_umber(), PigmentRole::Shadow),
                (builtin::quinacridone_rose(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    pub fn moss() -> Palette {
        Self::named(
            "moss",
            [
                (builtin::sap_green(), PigmentRole::BaseWash),
                (builtin::viridian(), PigmentRole::Shadow),
                (builtin::raw_sienna(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    pub fn slate() -> Palette {
        Self::named(
            "slate",
            [
                (builtin::paynes_grey(), PigmentRole::BaseWash),
                (builtin::lamp_black(), PigmentRole::Shadow),
                (builtin::cerulean(), PigmentRole::Accent),
                (builtin::moon_gold(), PigmentRole::Glow),
            ],
        )
    }

    /// The same roles with [`Pigment::luminous`] variants, for artwork that
    /// will be shown on a dark ground and should read as glow rather than as
    /// paint on black. Named `<name>_dark`; `by_name` resolves the suffix.
    pub fn for_dark_surface(&self) -> Palette {
        Palette::new(
            format!("{}_dark", self.name),
            self.entries
                .iter()
                .map(|e| PaletteEntry {
                    pigment: e.pigment.luminous(),
                    role: e.role,
                })
                .collect(),
        )
    }

    pub fn by_name(name: &str) -> Option<Palette> {
        if let Some(base) = name.strip_suffix("_dark") {
            return Self::by_name(base).map(|p| p.for_dark_surface());
        }
        match name {
            "moonlight" => Some(Self::moonlight()),
            "water" => Some(Self::water()),
            "dusk" => Some(Self::dusk()),
            "ember" => Some(Self::ember()),
            "moss" => Some(Self::moss()),
            "slate" => Some(Self::slate()),
            _ => None,
        }
    }

    fn named<const N: usize>(name: &str, entries: [(Pigment, PigmentRole); N]) -> Palette {
        Palette::new(
            name,
            entries
                .into_iter()
                .map(|(pigment, role)| PaletteEntry { pigment, role })
                .collect(),
        )
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn named_palettes_resolve_and_have_all_roles() {
        for name in Palette::NAMES {
            let p = Palette::by_name(name).expect(name);
            assert!(p.len() <= MAX_PIGMENTS);
            for role in [
                PigmentRole::BaseWash,
                PigmentRole::Shadow,
                PigmentRole::Accent,
                PigmentRole::Glow,
            ] {
                assert!(p.index_of(role).is_some(), "{name} lacks {role:?}");
            }
            let dark = Palette::by_name(&format!("{name}_dark")).expect("dark variant");
            assert_eq!(dark.name, format!("{name}_dark"));
            assert_eq!(dark.len(), p.len());
            assert_ne!(dark.entries[0].pigment, p.entries[0].pigment);
        }
    }
}
