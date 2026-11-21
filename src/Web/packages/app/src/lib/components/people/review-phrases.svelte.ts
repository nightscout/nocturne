import { formatNumber } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";
import type { Change } from "./review-format.js";

/** The change in time in range as a label for a screen reader. */
export function changeLabel(change: Change, periodDays: number): string {
  const days = formatNumber(periodDays);
  if (change.underOne) {
    return change.direction === "up"
      ? `Up less than 1 point from the previous ${days} days`
      : `Down less than 1 point from the previous ${days} days`;
  }
  const amount = plural(change.points, ["# point", "# points"]);
  switch (change.direction) {
    case "up":
      return `Up ${amount} from the previous ${days} days`;
    case "down":
      return `Down ${amount} from the previous ${days} days`;
    default:
      return `No change from the previous ${days} days`;
  }
}

/** The change in time in range as a sentence. */
export function changeSentence(change: Change, periodDays: number): string {
  const days = formatNumber(periodDays);
  if (change.underOne) {
    return change.direction === "up"
      ? `Up less than 1 point from the previous ${days} days.`
      : `Down less than 1 point from the previous ${days} days.`;
  }
  const amount = plural(change.points, ["# point", "# points"]);
  switch (change.direction) {
    case "up":
      return `Up ${amount} from the previous ${days} days.`;
    case "down":
      return `Down ${amount} from the previous ${days} days.`;
    default:
      return `No change from the previous ${days} days.`;
  }
}
