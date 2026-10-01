import { goto } from "$app/navigation";
import { resolve } from "$app/paths";

/** Opens Day in Review for the hovered time: what a click on a chart's time label does in the app. */
export function openDayInReview(time: Date | undefined): void {
  void goto(resolve(`/reports/day-in-review?date=${time}`));
}
