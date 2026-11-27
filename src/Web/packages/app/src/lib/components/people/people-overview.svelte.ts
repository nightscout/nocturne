import { getOverview } from "#lib/api/generated/myTenants.generated.remote.js";
import type { OverviewRuleSet } from "@nocturne/api-client";
import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
import { followOverview } from "./live-overview.svelte.js";
import { PERSON_POLL_MS } from "./person.js";
import { overviewRequest, type QueryState } from "./people-query.js";
import type { PeopleTab } from "./people-tab.js";

/** How soon the read runs again while the viewer's own flag rules are still being checked for some people. */
export const RULES_PENDING_REREAD_MS = 5_000;

/** A tab and the query it reads; null while the tab's own query is not known yet, as for a view not yet read. */
export type PeopleRead = { tab: PeopleTab; query: QueryState } | null;

/**
 * The overview read of a tab with its query, kept current by the one-minute poll and the `/overview`
 * feed. The read follows `read` as it changes, and there is none while it is null. `alongside` is read
 * again on each poll with it. `rules` names the flag rules the API judges beside the fixed reasons: the
 * page reads every rule that applies to the viewer, and the wall, which others in the room see too, reads
 * its teams' rules and not the viewer's own. While the API says rules or fact filters are still being
 * checked for some people, the read runs again after {@link RULES_PENDING_REREAD_MS}. Call during
 * component initialization.
 */
export function followPeople(read: () => PeopleRead, alongside: () => void, rules: OverviewRuleSet) {
  const query = $derived.by(() => {
    const current = read();
    return current ? getOverview(overviewRequest(current.tab, current.query, rules)) : null;
  });
  $effect(() => {
    if ((query?.current?.peopleWithRulesPending ?? 0) <= 0) return;
    const reread = setTimeout(() => void query?.refresh(), RULES_PENDING_REREAD_MS);
    return () => clearTimeout(reread);
  });
  // A bare await would answer with the value this page already holds.
  pollWhileVisible(() => {
    void query?.refresh();
    alongside();
  }, PERSON_POLL_MS);
  const feed = followOverview(() => void query?.refresh());
  return {
    get query() {
      return query;
    },
    get live() {
      return feed.live;
    },
  };
}
