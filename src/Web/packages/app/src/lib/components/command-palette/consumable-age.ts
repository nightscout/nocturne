import type {
  LegacyConsumableKind,
  LegacyCoverageDto,
  LegacyFallbackDto,
  TrackerInstanceDto,
} from "$lib/api";
import { ageHoursSince } from "$lib/components/trackers/age.svelte";

/**
 * The age the palette shows for a consumable at `now`: its legacy fallback's while no tracker
 * covers it, else the most recently started run of a definition the server says covers it.
 * Undefined when neither exists.
 */
export function consumableAgeHours(
  kind: LegacyConsumableKind,
  fallbacks: readonly LegacyFallbackDto[],
  coverage: readonly LegacyCoverageDto[],
  instances: readonly TrackerInstanceDto[],
  now: number
): number | undefined {
  const fallback = fallbacks.find((f) => f.kind === kind);
  if (fallback) return ageHoursSince(fallback.latestEventAt, now) ?? fallback.ageHours;

  const covering = coverage.find((c) => c.kind === kind)?.definitionIds ?? [];
  const latest = instances
    .filter((i) => i.definitionId && covering.includes(i.definitionId) && i.startedAt)
    .reduce<TrackerInstanceDto | undefined>(
      (newest, i) =>
        !newest || Date.parse(i.startedAt!) > Date.parse(newest.startedAt!)
          ? i
          : newest,
      undefined
    );
  return latest ? (ageHoursSince(latest.startedAt, now) ?? latest.ageHours) : undefined;
}
