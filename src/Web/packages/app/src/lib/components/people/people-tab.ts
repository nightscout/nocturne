/** What the People page lists: everyone the viewer follows, or one group the viewer is in. */
export type PeopleTab = { kind: "everyone" } | { kind: "group"; id: string };

export const EVERYONE: PeopleTab = { kind: "everyone" };

/** The tab the address names by its `group` parameter. */
export function parseTab(group: string | null): PeopleTab {
  return group ? { kind: "group", id: group } : EVERYONE;
}

/** A tab's value in the tab strip, unique across every kind of tab. */
export function tabValue(tab: PeopleTab): string {
  return tab.kind === "group" ? `group:${tab.id}` : "everyone";
}

export function tabOfValue(value: string): PeopleTab {
  return value.startsWith("group:") ? { kind: "group", id: value.slice("group:".length) } : EVERYONE;
}

/** The group the API reads for the tab; none for everyone. */
export function groupOf(tab: PeopleTab): string | undefined {
  return tab.kind === "group" ? tab.id : undefined;
}
