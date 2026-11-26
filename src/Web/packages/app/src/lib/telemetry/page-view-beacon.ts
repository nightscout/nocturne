import type { AfterNavigate } from "@sveltejs/kit";
import { PUBLIC_PAGE_COUNTS_ENABLED } from "$app/env/public";
import { recordPageView } from "#lib/api/generated/pageViews.generated.remote.js";

/**
 * Counts one view of the page a navigation arrived at, by its route id alone: no params, no query
 * string and nothing about the viewer. A navigation that stays on the same page (a changed query
 * or param) is not a new view. Nothing is sent while page counts are off, and a failed send is
 * dropped.
 */
export function sendPageView(
  navigation: Pick<AfterNavigate, "from" | "to">,
  enabled: boolean = PUBLIC_PAGE_COUNTS_ENABLED,
): void {
  if (!enabled) return;

  const routeId = navigation.to?.route.id;
  if (!routeId || routeId === navigation.from?.route.id) return;

  recordPageView({ routeId }).catch(() => {});
}
