import type { StaffGroup, TenantOverviewItem } from "@nocturne/api-client";
import { isFlagged } from "./person.js";
import type { WallSection } from "./wall-layout.js";

/** A heading over the wall's columns: one of the viewer's groups, or none for the people in none of them. */
export interface WallHeading {
  group: StaffGroup | null;
}

/**
 * The people of the wall's columns, in the API's order within each run. The flagged come first under no
 * heading. The others follow under the group the API names for each (`firstGroupId`), in the order of
 * `groups`, then the people in none of the viewer's groups. When fewer than two headings would have
 * anyone under them, or a person names a group `groups` does not hold yet, the others are one run under
 * no heading. A group with no id is skipped, as the tab strip skips it.
 */
export function wallSections(
  people: TenantOverviewItem[],
  groups: StaffGroup[] | undefined
): WallSection<TenantOverviewItem, WallHeading>[] {
  const others = people.filter((person) => !isFlagged(person));
  const runs = [
    ...(groups ?? []).filter((group) => group.id).map((group) => ({
      heading: { group },
      people: others.filter((person) => person.firstGroupId === group.id),
    })),
    { heading: { group: null }, people: others.filter((person) => !person.firstGroupId) },
  ].filter((run) => run.people.length > 0);
  const underHeadings = runs.reduce((count, run) => count + run.people.length, 0);
  const headed = runs.length > 1 && underHeadings === others.length;
  return [
    { heading: null, people: people.filter(isFlagged) },
    ...(headed ? runs : [{ heading: null, people: others }]),
  ].filter((section) => section.people.length > 0);
}
