import {
  PeopleFilterKind,
  TherapyType,
  type CustomReportFlagFact,
  type PeopleFilter,
  type StaffGroup,
} from "@nocturne/api-client";
import { formatList } from "#lib/utils/formatting.js";
import { plural } from "#lib/utils/plural.js";
import { ruleSentence } from "./flag-rule-text.svelte.js";

export const THERAPY_TYPES = [
  TherapyType.NoInsulin,
  TherapyType.AutomatedDelivery,
  TherapyType.Pump,
  TherapyType.Injections,
] as const;

export function therapyLabel(type: TherapyType): string {
  switch (type) {
    case TherapyType.NoInsulin:
      return "No insulin";
    case TherapyType.AutomatedDelivery:
      return "Automated delivery";
    case TherapyType.Pump:
      return "Pump";
    default:
      return "Injections";
  }
}

/**
 * What a filter's sentence names. Each is undefined while its list is read, and null when the list does
 * not hold it or did not load. A data filter's value is stated only with its fact, which gives its unit.
 */
export interface FilterNames {
  fact?: CustomReportFlagFact | null;
  teamName?: string | null;
  group?: StaffGroup | null;
}

/** A filter as one sentence, on its chip. */
export function filterSentence(filter: PeopleFilter, names: FilterNames): string {
  switch (filter.kind) {
    case PeopleFilterKind.Fact:
      if (names.fact) return ruleSentence(filter, names.fact.field.label, names.fact.field.unit);
      return names.fact === null ? "A filter by a figure of their data" : "Loading the data filter…";
    case PeopleFilterKind.AgeAtLeast:
      return plural(filter.years ?? 0, ["Age is at least # year", "Age is at least # years"]);
    case PeopleFilterKind.AgeAtMost:
      return plural(filter.years ?? 0, ["Age is at most # year", "Age is at most # years"]);
    case PeopleFilterKind.Therapy:
      return `Therapy type: ${formatList((filter.therapyTypes ?? []).map(therapyLabel), "disjunction")}`;
    case PeopleFilterKind.Flagged:
      return "Flagged only";
    case PeopleFilterKind.SourceDirect:
      return "Followed directly";
    case PeopleFilterKind.SourceTeam:
      if (names.teamName) return `Followed through ${names.teamName}`;
      return names.teamName === null ? "Followed through a team that is not one of your teams" : "Followed through a team";
    default:
      if (names.group?.sharesName) return `In the group ${names.group.name}, ${names.group.teamName}`;
      if (names.group) return `In the group ${names.group.name}`;
      return names.group === null ? "In a group you are no longer in" : "In a group";
  }
}
