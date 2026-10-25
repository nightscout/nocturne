import {
  NotificationUrgency,
  type LegacyFallbackDto,
  type TrackerInstanceDto,
  type TrackerThresholdTimeDto,
} from "$api";

const URGENCY_RANK: Record<NotificationUrgency, number> = {
  [NotificationUrgency.Info]: 0,
  [NotificationUrgency.Warn]: 1,
  [NotificationUrgency.Hazard]: 2,
  [NotificationUrgency.Urgent]: 3,
};

/** A running tracker, or a legacy age read as the tracker converting it would run. */
type Scheduled = Pick<TrackerInstanceDto, "schedule"> | Pick<LegacyFallbackDto, "schedule">;

/** Orders urgencies for comparison; -1 for none. */
export function urgencyRank(urgency: NotificationUrgency | null | undefined): number {
  return urgency ? URGENCY_RANK[urgency] : -1;
}

/**
 * The most urgent step of a schedule that has fired by `now`. The schedule's times come resolved
 * from the server, exactly as the steps' alert rules fire, so this only compares them with the
 * clock.
 */
export function reachedStep(
  scheduled: Scheduled,
  now: number
): TrackerThresholdTimeDto | undefined {
  let reached: TrackerThresholdTimeDto | undefined;
  for (const step of scheduled.schedule ?? []) {
    if (!step.firesAt || Date.parse(step.firesAt) > now) continue;
    if (urgencyRank(step.urgency) > urgencyRank(reached?.urgency)) reached = step;
  }
  return reached;
}

/** The urgency of {@link reachedStep}, or null before the first step fires. */
export function reachedUrgency(scheduled: Scheduled, now: number): NotificationUrgency | null {
  return reachedStep(scheduled, now)?.urgency ?? null;
}
