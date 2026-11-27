import { PUBLIC_PAGE_COUNTS_ENABLED } from "$app/env/public";
import { recordPageView } from "#lib/api/generated/pageViews.generated.remote.js";

export interface PageViewBeacon {
  /**
   * Reports the page the app is on, by route id (null for a page with no route, such as a 404).
   * Counts a view when this is not the page last reported, so the navigation that arrives at a
   * page and the layout mounting on it can both report it and one view is counted.
   */
  arrived(routeId: string | null | undefined): void;
}

/**
 * Counts one view of each page the visitor arrives at, by its route id alone: no params, no query
 * string and nothing about the viewer. A navigation that stays on the same page (a changed query
 * or param) is not a new view. Nothing is sent while page counts are off, and a failed send is
 * dropped.
 *
 * SvelteKit calls an `afterNavigate` callback for the first page of a visit a microtask after it
 * hydrates, and a callback registers when its layout mounts. With async components the layout does
 * not mount until the page's own `await`s settle, which is after that call, so the first page is
 * reported by the layout's mount instead.
 */
export function createPageViewBeacon(enabled: boolean = PUBLIC_PAGE_COUNTS_ENABLED): PageViewBeacon {
  let last: string | null = null;

  return {
    arrived(routeId) {
      if (!enabled) return;
      if (!routeId) {
        last = null;
        return;
      }
      if (routeId === last) return;

      last = routeId;
      recordPageView({ routeId }).catch(() => {});
    },
  };
}
