/**
 * Meal time constants for determining meal labels based on time of day.
 * These will be refactored later to a configurable backend setting.
 */

export interface MealTimeRange {
  name: string;
  /** Start hour (0-23) */
  startHour: number;
  /** End hour (0-23, exclusive) */
  endHour: number;
}

/**
 * Default meal time ranges based on typical eating schedules.
 * Times are in 24-hour format.
 */
export const MEAL_TIME_RANGES: MealTimeRange[] = [
  { name: "Breakfast", startHour: 5, endHour: 11 },
  { name: "Lunch", startHour: 11, endHour: 15 },
  { name: "Snack", startHour: 15, endHour: 17 },
  { name: "Dinner", startHour: 17, endHour: 21 },
  { name: "Late Night", startHour: 21, endHour: 5 },
];

/**
 * Get the meal name for a given date based on time of day.
 * @param date The date/time to check
 * @returns The meal name (e.g., "Breakfast", "Lunch", "Dinner")
 */
export function getMealNameForTime(date: Date): string {
  const hour = date.getHours();

  for (const range of MEAL_TIME_RANGES) {
    // Handle overnight ranges (e.g., Late Night 21-5)
    if (range.startHour > range.endHour) {
      if (hour >= range.startHour || hour < range.endHour) {
        return range.name;
      }
    } else {
      if (hour >= range.startHour && hour < range.endHour) {
        return range.name;
      }
    }
  }

  // Default fallback
  return "Meal";
}

/**
 * Check if a time falls within a specific meal period.
 * @param date The date/time to check
 * @param mealName The meal name to check for
 * @returns True if the time is within that meal period
 */
export function isTimeInMealPeriod(date: Date, mealName: string): boolean {
  const hour = date.getHours();
  const range = MEAL_TIME_RANGES.find((r) => r.name === mealName);

  if (!range) return false;

  // Handle overnight ranges
  if (range.startHour > range.endHour) {
    return hour >= range.startHour || hour < range.endHour;
  }

  return hour >= range.startHour && hour < range.endHour;
}
