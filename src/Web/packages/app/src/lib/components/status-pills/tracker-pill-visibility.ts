import type { TrackerDefinitionDto, TrackerInstanceDto } from "$lib/api";
import { DashboardVisibility, NotificationUrgency } from "$lib/api";
import { reachedUrgency, urgencyRank } from "$lib/components/trackers/schedule";

/** The step a pill waits for before it appears; null for always, undefined for never. */
const SHOWN_FROM: Record<DashboardVisibility, NotificationUrgency | null | undefined> = {
  [DashboardVisibility.Off]: undefined,
  [DashboardVisibility.Always]: null,
  [DashboardVisibility.Info]: NotificationUrgency.Info,
  [DashboardVisibility.Warn]: NotificationUrgency.Warn,
  [DashboardVisibility.Hazard]: NotificationUrgency.Hazard,
  [DashboardVisibility.Urgent]: NotificationUrgency.Urgent,
};

export function trackerDefinition(
  instance: TrackerInstanceDto,
  definitions: readonly TrackerDefinitionDto[]
): TrackerDefinitionDto | undefined {
  return definitions.find((d) => d.id === instance.definitionId);
}

/** The instances whose definition shows a pill on the dashboard at `now`. */
export function visibleTrackerInstances(
  instances: readonly TrackerInstanceDto[],
  definitions: readonly TrackerDefinitionDto[],
  now: number
): TrackerInstanceDto[] {
  return instances.filter((instance) => {
    const def = trackerDefinition(instance, definitions);
    if (!def) return false;
    const shownFrom = SHOWN_FROM[def.dashboardVisibility ?? DashboardVisibility.Always];
    if (shownFrom === undefined) return false;
    if (shownFrom === null) return true;
    return urgencyRank(reachedUrgency(instance, now)) >= urgencyRank(shownFrom);
  });
}
