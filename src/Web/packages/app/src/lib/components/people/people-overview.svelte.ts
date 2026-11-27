import { getOverview } from "#lib/api/generated/myTenants.generated.remote.js";
import type { OverviewRuleSet } from "@nocturne/api-client";
import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
import { followOverview } from "./live-overview.svelte.js";
import { PERSON_POLL_MS } from "./person.js";

/** How soon the read runs again while the viewer's own flag rules are still being checked for some people. */
export const RULES_PENDING_REREAD_MS = 5_000;

/**
 * The overview read of the people the viewer follows, or of one group they are in when `group` names
 * one, kept current by the one-minute poll and the `/overview` feed. The read follows `group` as it
 * changes. `alongside` is read again on each poll with it. `rules` names the flag rules the API judges
 * beside the fixed reasons: a view others see too reads only the fixed set. While the API says rules are
 * still being checked for some people, the read runs again after {@link RULES_PENDING_REREAD_MS}. Call
 * during component initialization.
 */
export function followPeople(
  group: () => string | undefined,
  alongside: () => void,
  rules: OverviewRuleSet
) {
  const query = $derived.by(() => {
    const id = group();
    return getOverview(id ? { group: id, rules } : { rules });
  });
  $effect(() => {
    if ((query.current?.peopleWithRulesPending ?? 0) <= 0) return;
    const reread = setTimeout(() => void query.refresh(), RULES_PENDING_REREAD_MS);
    return () => clearTimeout(reread);
  });
  // A bare await would answer with the value this page already holds.
  pollWhileVisible(() => {
    void query.refresh();
    alongside();
  }, PERSON_POLL_MS);
  const feed = followOverview(() => query.refresh());
  return {
    get query() {
      return query;
    },
    get live() {
      return feed.live;
    },
  };
}
