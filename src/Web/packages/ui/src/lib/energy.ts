/**
 * Food energy unit conversion. Energy is stored in kilojoules and shown in the user's unit,
 * which the backend resolves.
 */

export const ENERGY_UNITS = ["kJ", "kcal"] as const;

/** Display unit preference. */
export type EnergyUnit = (typeof ENERGY_UNITS)[number];

/**
 * Kilojoules in one kilocalorie. Must equal `NutritionConstants.KilojoulesPerKilocalorie`;
 * `GlucoseMirrorTests` fails if it does not.
 */
export const KJ_PER_KCAL = 4.184;

/** Stored kilojoules in the given display unit, to the nearest whole unit. */
export function energyToDisplayUnits(kilojoules: number, unit: EnergyUnit): number {
  return Math.round(unit === "kcal" ? kilojoules / KJ_PER_KCAL : kilojoules);
}

/** A value in the given display unit as kilojoules for storage. */
export function energyFromDisplayUnits(value: number, unit: EnergyUnit): number {
  return unit === "kcal" ? value * KJ_PER_KCAL : value;
}
