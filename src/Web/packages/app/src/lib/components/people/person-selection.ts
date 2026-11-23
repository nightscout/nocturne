import { getContext, setContext } from "svelte";
import type { TenantOverviewItem } from "@nocturne/api-client";

export interface PersonSelection {
  readonly selectedSlug: string | null;
  /** Where the row's link points: this page with the person open. */
  hrefFor(person: TenantOverviewItem): string;
  select(person: TenantOverviewItem): void;
}

const KEY = "nocturne:people:person-selection";

export function setPersonSelection(selection: PersonSelection): void {
  setContext(KEY, selection);
}

export function getPersonSelection(): PersonSelection {
  return getContext<PersonSelection>(KEY);
}
