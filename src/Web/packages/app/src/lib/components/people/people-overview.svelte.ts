import { getOverview } from "#lib/api/generated/myTenants.generated.remote.js";
import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
import { followOverview } from "./live-overview.svelte.js";
import { PERSON_POLL_MS } from "./person.js";

/**
 * The overview read of the people the viewer follows, or of one group they are in when `group` names
 * one, kept current by the one-minute poll and the `/overview` feed. The read follows `group` as it
 * changes. `alongside` is read again on each poll with it. Call during component initialization.
 */
export function followPeople(group: () => string | undefined, alongside: () => void) {
  const query = $derived.by(() => {
    const id = group();
    return getOverview(id ? { group: id } : undefined);
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
