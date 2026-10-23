import type { Food } from "$lib/api";

/** Standard food measurement units */
export const FOOD_UNITS = ["g", "ml", "pcs", "oz", "serving"] as const;
export type FoodUnit = (typeof FOOD_UNITS)[number];

/** Glycemic index options */
export const GI_OPTIONS = [
  { value: 1, label: "Low" },
  { value: 2, label: "Medium" },
  { value: 3, label: "High" },
] as const;

export type GiValue = (typeof GI_OPTIONS)[number]["value"];

/** Get GI label from numeric value */
export function getGiLabel(value: number | undefined): string {
  return GI_OPTIONS.find((opt) => opt.value === value)?.label ?? "Unknown";
}

/** Category name to its subcategory names, as the category combobox reads it. */
export type FoodCategoryMap = Record<string, Record<string, boolean>>;

export function foodCategoryMap(foods: Food[]): FoodCategoryMap {
  const map: FoodCategoryMap = {};
  for (const food of foods) {
    if (!food.category) continue;
    map[food.category] ??= {};
    if (food.subcategory) map[food.category][food.subcategory] = true;
  }
  return map;
}

/**
 * A food to create from another: what it is and what it contains, without the source's identity,
 * visibility or quickpick list.
 */
export function newFoodFrom(food: Food): Food {
  const {
    _id: _,
    hidden: _hidden,
    hideafteruse: _hideafteruse,
    position: _position,
    foods: _foods,
    created_at: _createdAt,
    srvCreated: _srvCreated,
    srvModified: _srvModified,
    ...copy
  } = food;
  return copy;
}

/** Default portion size for new foods */
export const DEFAULT_PORTION = 100;

/** Default unit for new foods */
export const DEFAULT_UNIT = "g";

/** Default GI for new foods (Medium) */
export const DEFAULT_GI: GiValue = 2;

/**
 * Scale macronutrients based on portion ratio.
 *
 * If a food is defined with 4 servings but the user wants 1 serving,
 * all macros should be divided by 4.
 *
 * @param basePortion - The portion size defined in the food record
 * @param selectedPortion - The portion size the user wants
 * @param macroValue - The macro value at the base portion
 * @returns The scaled macro value
 */
export function scaleMacro(
  basePortion: number,
  selectedPortion: number,
  macroValue: number
): number {
  if (basePortion <= 0 || selectedPortion <= 0) return 0;
  return (macroValue / basePortion) * selectedPortion;
}

/**
 * Scale all macronutrients for a food based on portion ratio
 */
export function scaleAllMacros(
  basePortion: number,
  selectedPortion: number,
  macros: { carbs: number; fat: number; protein: number; energy: number }
): { carbs: number; fat: number; protein: number; energy: number } {
  return {
    carbs: scaleMacro(basePortion, selectedPortion, macros.carbs),
    fat: scaleMacro(basePortion, selectedPortion, macros.fat),
    protein: scaleMacro(basePortion, selectedPortion, macros.protein),
    energy: scaleMacro(basePortion, selectedPortion, macros.energy),
  };
}

/**
 * Format a macro value for display (rounds to 1 decimal place)
 */
export function formatMacro(value: number): string {
  return value.toFixed(1);
}
