import { MealPeriod } from "$lib/api";

/** Built per call so wuchale reads them in the current locale. */
export function mealPeriodLabel(period: MealPeriod | null | undefined): string | undefined {
  switch (period) {
    case MealPeriod.Breakfast:
      return "Breakfast";
    case MealPeriod.Lunch:
      return "Lunch";
    case MealPeriod.Snack:
      return "Snack";
    case MealPeriod.Dinner:
      return "Dinner";
    case MealPeriod.LateNight:
      return "Late night";
    default:
      return undefined;
  }
}

/** Food names win over the meal period, which only stands in when none were logged. */
export function carbMarkerName(marker: {
  label?: string | null;
  mealPeriod?: MealPeriod | null;
}): string | undefined {
  return marker.label || mealPeriodLabel(marker.mealPeriod);
}
