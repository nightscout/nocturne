import type { PaletteId } from '../types';

/**
 * A palette's four pigments as the engine draws them.
 *
 * These are the `r_white` reflectances of `builtin::*` in
 * `crates/nocturne-watercolour-core/src/domain/pigment.rs`, which the render
 * path writes out untransformed. Keeping them here means a surface can take
 * its colour from the paint without reading a single pixel back.
 */
export interface PigmentColours {
  baseWash: string;
  shadow: string;
  accent: string;
  glow: string;
}

const INDIGO = 'rgb(41, 51, 92)';
const PAYNES_GREY = 'rgb(56, 64, 82)';
const PHTHALO_BLUE = 'rgb(20, 71, 158)';
const CERULEAN = 'rgb(77, 140, 199)';
const QUINACRIDONE_ROSE = 'rgb(184, 46, 92)';
const RAW_SIENNA = 'rgb(168, 112, 46)';
const BURNT_UMBER = 'rgb(77, 48, 31)';
const SAP_GREEN = 'rgb(71, 112, 36)';
const VIRIDIAN = 'rgb(31, 117, 102)';
const LAMP_BLACK = 'rgb(36, 36, 38)';
const MOON_GOLD = 'rgb(245, 219, 148)';
const EMBER_ORANGE = 'rgb(230, 107, 31)';

export const PALETTE_PIGMENTS: Readonly<Record<PaletteId, PigmentColours>> = {
  moonlight: { baseWash: INDIGO, shadow: PAYNES_GREY, accent: QUINACRIDONE_ROSE, glow: MOON_GOLD },
  water: { baseWash: CERULEAN, shadow: PHTHALO_BLUE, accent: VIRIDIAN, glow: MOON_GOLD },
  dusk: { baseWash: QUINACRIDONE_ROSE, shadow: INDIGO, accent: EMBER_ORANGE, glow: MOON_GOLD },
  ember: { baseWash: EMBER_ORANGE, shadow: BURNT_UMBER, accent: QUINACRIDONE_ROSE, glow: MOON_GOLD },
  moss: { baseWash: SAP_GREEN, shadow: VIRIDIAN, accent: RAW_SIENNA, glow: MOON_GOLD },
  slate: { baseWash: PAYNES_GREY, shadow: LAMP_BLACK, accent: CERULEAN, glow: MOON_GOLD },
};

/**
 * The background wash a surface takes from the mark that landed on it.
 *
 * Low enough to read as the paper having warmed toward the paint rather than
 * as a coloured card. The caller multiplies `alpha` by the reveal's own curve
 * so the surface and the mark arrive together.
 */
export function surfaceTint(palette: PaletteId, alpha: number): string {
  const rgb = PALETTE_PIGMENTS[palette].baseWash;
  return rgb.replace(/^rgb\((.*)\)$/, (_, channels: string) => `rgba(${channels}, ${alpha.toFixed(3)})`);
}

/** How much of the paint's colour a tinted surface takes at full reveal. */
export const SURFACE_TINT_ALPHA = 0.06;
