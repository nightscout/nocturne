import { getOverview } from "#lib/api/generated/myTenants.generated.remote.js";
import { pollWhileVisible } from "#lib/utils/poll-while-visible.svelte.js";
import { followOverview } from "./live-overview.svelte.js";
import { PERSON_POLL_MS } from "./person.js";

/**
 * The overview read of the people the viewer follows, kept current by the one-minute poll and the
 * `/overview` feed. Call during component initialization.
 */
export function followPeople() {
  const query = getOverview();
  // A bare await would answer with the value this page already holds.
  pollWhileVisible(() => query.refresh(), PERSON_POLL_MS);
  const feed = followOverview(() => query.refresh());
  return {
    query,
    get live() {
      return feed.live;
    },
  };
}
