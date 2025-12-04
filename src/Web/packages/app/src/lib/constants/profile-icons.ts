/**
 * Profile Icon Constants
 *
 * Available icons for profile customization, using Lucide icon names
 */

export const PROFILE_ICONS = [
  { id: "user", name: "User", description: "Default person icon" },
  { id: "user-circle", name: "User Circle", description: "Person in a circle" },
  { id: "heart", name: "Heart", description: "Heart shape" },
  { id: "heart-pulse", name: "Heart Pulse", description: "Heart with pulse line" },
  { id: "activity", name: "Activity", description: "Activity/vitals line" },
  { id: "syringe", name: "Syringe", description: "Medical syringe" },
  { id: "pill", name: "Pill", description: "Medicine pill" },
  { id: "droplet", name: "Droplet", description: "Blood drop" },
  { id: "target", name: "Target", description: "Bullseye target" },
  { id: "sun", name: "Sun", description: "Daytime/morning" },
  { id: "moon", name: "Moon", description: "Nighttime/evening" },
  { id: "sunrise", name: "Sunrise", description: "Morning routine" },
  { id: "sunset", name: "Sunset", description: "Evening routine" },
  { id: "dumbbell", name: "Dumbbell", description: "Exercise/workout" },
  { id: "bike", name: "Bike", description: "Cycling/activity" },
  { id: "footprints", name: "Footprints", description: "Walking/steps" },
  { id: "utensils", name: "Utensils", description: "Meals/eating" },
  { id: "coffee", name: "Coffee", description: "Morning coffee" },
  { id: "cake", name: "Cake", description: "Special occasion" },
  { id: "baby", name: "Baby", description: "Child profile" },
  { id: "briefcase", name: "Briefcase", description: "Work profile" },
  { id: "home", name: "Home", description: "Home routine" },
  { id: "plane", name: "Plane", description: "Travel profile" },
  { id: "zap", name: "Zap", description: "High intensity" },
  { id: "shield", name: "Shield", description: "Protected/safe" },
  { id: "star", name: "Star", description: "Favorite" },
  { id: "sparkles", name: "Sparkles", description: "Special" },
  { id: "clock", name: "Clock", description: "Time-based" },
  { id: "calendar", name: "Calendar", description: "Schedule-based" },
  { id: "trending-up", name: "Trending Up", description: "Growth/improvement" },
] as const;

export type ProfileIconId = typeof PROFILE_ICONS[number]["id"];

export const DEFAULT_PROFILE_ICON: ProfileIconId = "user";

/**
 * Get icon metadata by ID
 */
export function getProfileIcon(iconId: string | undefined): typeof PROFILE_ICONS[number] | undefined {
  if (!iconId) return PROFILE_ICONS.find(i => i.id === DEFAULT_PROFILE_ICON);
  return PROFILE_ICONS.find(i => i.id === iconId);
}

/**
 * Get icon name for display
 */
export function getProfileIconName(iconId: string | undefined): string {
  const icon = getProfileIcon(iconId);
  return icon?.name ?? "User";
}

/**
 * Common timezones for profile settings
 */
export const COMMON_TIMEZONES = [
  "America/New_York",
  "America/Chicago",
  "America/Denver",
  "America/Los_Angeles",
  "America/Anchorage",
  "Pacific/Honolulu",
  "Europe/London",
  "Europe/Paris",
  "Europe/Berlin",
  "Europe/Moscow",
  "Asia/Tokyo",
  "Asia/Shanghai",
  "Asia/Singapore",
  "Australia/Sydney",
  "Pacific/Auckland",
] as const;

/**
 * Units options for blood glucose
 */
export const BG_UNITS = [
  { value: "mg/dL", label: "mg/dL", description: "Milligrams per deciliter" },
  { value: "mmol", label: "mmol/L", description: "Millimoles per liter" },
] as const;

export type BgUnitsValue = typeof BG_UNITS[number]["value"];
