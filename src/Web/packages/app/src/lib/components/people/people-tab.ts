import type { StaffGroup } from "@nocturne/api-client";

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

/** `url` naming `tab` in its address, the People page's and the wall's alike. */
export function withTab(url: URL, tab: PeopleTab): URL {
  url.searchParams.delete("group");
  if (tab.kind === "group") url.searchParams.set("group", tab.id);
  return url;
}

/** Why a group's tab shows everyone: the viewer is not in the group, or their groups did not load. */
export type FallbackReason = "unknown" | "unread";

/**
 * Whether the group `id` falls back to everyone. `listed` is the viewer's groups, undefined until they
 * load; `notFound` is the group's own read answering that the viewer is not in it.
 */
export function fallbackOf(
  id: string,
  listed: StaffGroup[] | undefined,
  groupsFailed: boolean,
  notFound: boolean
): FallbackReason | null {
  if (listed === undefined && groupsFailed) return "unread";
  return (listed !== undefined && !listed.some((group) => group.id === id)) || notFound
    ? "unknown"
    : null;
}
