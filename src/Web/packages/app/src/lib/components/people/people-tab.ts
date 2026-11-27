/** What the People page lists: everyone the viewer follows, one group the viewer is in, or one of their views. */
export type PeopleTab =
  | { kind: "everyone" }
  | { kind: "group"; id: string }
  | { kind: "view"; id: string };

export const EVERYONE: PeopleTab = { kind: "everyone" };

/** The tab an address names by its `group` or its `view`. An address that names both is malformed and names neither. */
export function tabOf(searchParams: Pick<URLSearchParams, "get">): PeopleTab {
  const group = searchParams.get("group");
  const view = searchParams.get("view");
  if (group && view) return EVERYONE;
  if (group) return { kind: "group", id: group };
  if (view) return { kind: "view", id: view };
  return EVERYONE;
}

/** A tab's value in the tab strip, unique across every kind of tab. */
export function tabValue(tab: PeopleTab): string {
  switch (tab.kind) {
    case "group":
      return `group:${tab.id}`;
    case "view":
      return `view:${tab.id}`;
    default:
      return "everyone";
  }
}

export function tabOfValue(value: string): PeopleTab {
  if (value.startsWith("group:")) return { kind: "group", id: value.slice("group:".length) };
  if (value.startsWith("view:")) return { kind: "view", id: value.slice("view:".length) };
  return EVERYONE;
}

/** The group the API reads for the tab. A view starts from everyone, so it names none. */
export function groupOf(tab: PeopleTab): string | undefined {
  return tab.kind === "group" ? tab.id : undefined;
}

export function viewOf(tab: PeopleTab): string | undefined {
  return tab.kind === "view" ? tab.id : undefined;
}

/** `url` naming `tab` in its address, the People page's and the wall's alike: a group or a view, never both. */
export function withTab(url: URL, tab: PeopleTab): URL {
  url.searchParams.delete("group");
  url.searchParams.delete("view");
  if (tab.kind === "group") url.searchParams.set("group", tab.id);
  if (tab.kind === "view") url.searchParams.set("view", tab.id);
  return url;
}

/** Why a group's or a view's tab shows everyone: the viewer has no such one, or their list did not load. */
export type FallbackReason = "unknown" | "unread";

export interface Fallback {
  kind: "group" | "view";
  reason: FallbackReason;
}

/**
 * Whether the group or view `id` falls back to everyone. `listed` is the viewer's own groups or views,
 * undefined until they load; `notFound` is a group's own read answering that the viewer is not in it.
 */
export function fallbackOf(
  id: string,
  listed: readonly { id?: string }[] | undefined,
  listFailed: boolean,
  notFound: boolean
): FallbackReason | null {
  if (listed === undefined && listFailed) return "unread";
  return (listed !== undefined && !listed.some((entry) => entry.id === id)) || notFound
    ? "unknown"
    : null;
}
