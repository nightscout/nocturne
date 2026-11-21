import {
  Direction,
  GlucoseStatus,
  TenantOverviewAttentionKind,
  type TenantOverviewAttention,
  type TenantOverviewItem,
} from "@nocturne/api-client";
import { plural } from "#lib/utils/plural.js";
import { canonicalDirection } from "@nocturne/ui/glucose";

function durationText(minutes: number): string {
  if (minutes < 60) return `${minutes} min`;
  const hours = Math.round(minutes / 60);
  if (hours < 24) return `${hours} h`;
  return plural(Math.round(hours / 24), ["# day", "# days"]);
}

type Trend = "none" | "risingFast" | "rising" | "steady" | "falling" | "fallingFast";

/** Grouped as `deltaColorClass` groups directions: fast, regular, flat. */
function trendOf(direction: string | undefined): Trend {
  switch (canonicalDirection(direction)) {
    case Direction.TripleUp:
    case Direction.DoubleUp:
      return "risingFast";
    case Direction.SingleUp:
    case Direction.FortyFiveUp:
      return "rising";
    case Direction.Flat:
      return "steady";
    case Direction.SingleDown:
    case Direction.FortyFiveDown:
      return "falling";
    case Direction.DoubleDown:
    case Direction.TripleDown:
      return "fallingFast";
    default:
      return "none";
  }
}

function outOfRangePhrase(person: TenantOverviewItem): string {
  const trend = trendOf(person.latest?.direction);
  switch (person.status) {
    case GlucoseStatus.UrgentLow:
      switch (trend) {
        case "risingFast":
          return "Very low, rising fast";
        case "rising":
          return "Very low, rising";
        case "steady":
          return "Very low, steady";
        case "falling":
          return "Very low, falling";
        case "fallingFast":
          return "Very low, falling fast";
        default:
          return "Very low";
      }
    case GlucoseStatus.Low:
      switch (trend) {
        case "risingFast":
          return "Low, rising fast";
        case "rising":
          return "Low, rising";
        case "steady":
          return "Low, steady";
        case "falling":
          return "Low, falling";
        case "fallingFast":
          return "Low, falling fast";
        default:
          return "Low";
      }
    case GlucoseStatus.High:
      switch (trend) {
        case "risingFast":
          return "High, rising fast";
        case "rising":
          return "High, rising";
        case "steady":
          return "High, steady";
        case "falling":
          return "High, falling";
        case "fallingFast":
          return "High, falling fast";
        default:
          return "High";
      }
    case GlucoseStatus.UrgentHigh:
      switch (trend) {
        case "risingFast":
          return "Very high, rising fast";
        case "rising":
          return "Very high, rising";
        case "steady":
          return "Very high, steady";
        case "falling":
          return "Very high, falling";
        case "fallingFast":
          return "Very high, falling fast";
        default:
          return "Very high";
      }
    default:
      return "Out of range";
  }
}

function trackerPhrase(reason: TenantOverviewAttention): string {
  const name = reason.trackerName || "Tracker";
  const hours = reason.hoursUntilEvent;
  if (hours == null) return `${name} due`;
  if (hours <= 0) return `${name} overdue`;
  return `${name} due in ${durationText(Math.max(1, Math.round(hours * 60)))}`;
}

export function attentionPhrase(
  reason: TenantOverviewAttention,
  person: TenantOverviewItem
): string {
  switch (reason.kind) {
    case TenantOverviewAttentionKind.GlucoseOutOfRange:
      return outOfRangePhrase(person);
    case TenantOverviewAttentionKind.NoRecentReading:
      return reason.minutesSinceReading == null
        ? "No data"
        : `No data for ${durationText(reason.minutesSinceReading)}`;
    case TenantOverviewAttentionKind.ActiveAlert: {
      const count = person.activeAlertCount;
      return count == null
        ? "Active alert"
        : plural(count, ["# active alert", "# active alerts"]);
    }
    case TenantOverviewAttentionKind.TrackerDue:
      return trackerPhrase(reason);
    default:
      return "";
  }
}
