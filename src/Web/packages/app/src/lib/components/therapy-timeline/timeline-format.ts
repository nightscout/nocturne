import { ProfileAttribute, type ProfileChange, type ProfileVersion } from "#lib/api/index.js";
import {
  bg,
  bgLabel,
  formatInsulinToCarbRatio,
  time,
} from "#lib/utils/formatting.js";

export const DAY_MS = 86_400_000;

/** The schedules in the order the page draws them. */
export const ATTRIBUTES = [
  ProfileAttribute.Basal,
  ProfileAttribute.Sensitivity,
  ProfileAttribute.CarbRatio,
  ProfileAttribute.TargetRange,
] as const;

export function attributeLabel(attribute: ProfileAttribute | undefined): string {
  switch (attribute) {
    case ProfileAttribute.Basal:
      return "Basal";
    case ProfileAttribute.Sensitivity:
      return "Sensitivity";
    case ProfileAttribute.CarbRatio:
      return "Carb ratio";
    case ProfileAttribute.TargetRange:
      return "Target";
    default:
      return "";
  }
}

export function attributeUnit(attribute: ProfileAttribute | undefined): string {
  switch (attribute) {
    case ProfileAttribute.Basal:
      return "U/h";
    case ProfileAttribute.Sensitivity:
      return `${bgLabel()}/U`;
    case ProfileAttribute.TargetRange:
      return bgLabel();
    default:
      return "";
  }
}

/** A value of a schedule, without its unit except for a carb ratio, whose format carries it. */
export function attributeValue(
  attribute: ProfileAttribute | undefined,
  value: number | null | undefined,
  high?: number | null
): string {
  if (value == null) return "–";
  switch (attribute) {
    case ProfileAttribute.Basal:
      return value.toFixed(2);
    case ProfileAttribute.Sensitivity:
      return String(bg(value));
    case ProfileAttribute.CarbRatio:
      return formatInsulinToCarbRatio(value);
    case ProfileAttribute.TargetRange:
      return high != null && high !== value ? `${bg(value)}–${bg(high)}` : String(bg(value));
    default:
      return String(value);
  }
}

/** A time of day given as seconds from midnight, in the user's clock format. */
export function timeOfDay(seconds: number | undefined): string {
  const s = seconds ?? 0;
  return time(new Date(2000, 0, 1, Math.floor(s / 3600), Math.floor((s % 3600) / 60)), {
    compact: true,
  });
}

/** A profile's name with the percentage and time shift that changed its schedules. */
export function profileLabel(
  name: string | undefined,
  percentage: number | null | undefined,
  timeshiftHours: number | null | undefined
): string {
  const parts = [name || "Default"];
  if (percentage != null && percentage !== 100) parts.push(`${percentage}%`);
  if (timeshiftHours) parts.push(`${timeshiftHours > 0 ? "+" : ""}${timeshiftHours} h`);
  return parts.join(" ");
}

export function versionLabel(version: ProfileVersion): string {
  return profileLabel(version.profileName, version.percentage, version.timeshiftHours);
}

/** Whether a change is a profile switch, which the API marks with the activation that starts at it. */
export function isSwitch(change: ProfileChange): boolean {
  return change.activation != null;
}

/** A duration in days and hours, for stretches that run from hours to months. */
export function spanLength(startMills: number, endMills: number): string {
  const hours = Math.max(0, Math.round((endMills - startMills) / 3_600_000));
  if (hours < 1) return "< 1 h";
  if (hours < 48) return `${hours} h`;
  return `${Math.round(hours / 24)} d`;
}

/** The fraction of a window a time is at, 0 to 1. */
export function fractionOf(mills: number, from: number, to: number): number {
  return Math.min(1, Math.max(0, (mills - from) / (to - from)));
}
