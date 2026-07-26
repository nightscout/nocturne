import { getActiveAlerts } from "$api/generated/alerts.generated.remote";

/** How often to re-read the active-alert surface while the tab is visible. */
const POLL_MS = 10_000;

/**
 * Keeps the shared `getActiveAlerts` query fresh.
 *
 * Call this exactly once, from the layout that mounts the alert surfaces. Every
 * component that reads `getActiveAlerts().current` sees the refreshed data, so a
 * second caller would just double the request rate for the same result — which
 * is what happened when the banner and the fresh-fire toast each ran their own
 * timer at different cadences.
 *
 * Polling stops while the tab is hidden and catches up on return, so a
 * backgrounded tab isn't billing requests for a surface nobody can see.
 */
export function pollActiveAlerts(): void {
  $effect(() => {
    let timer: ReturnType<typeof setInterval> | null = null;

    function stop() {
      if (timer) clearInterval(timer);
      timer = null;
    }

    function start() {
      if (timer) return;
      timer = setInterval(() => getActiveAlerts().refresh(), POLL_MS);
    }

    function onVisibilityChange() {
      if (document.hidden) {
        stop();
      } else {
        // Whatever fired while hidden is unseen, so catch up immediately.
        getActiveAlerts().refresh();
        start();
      }
    }

    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      stop();
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  });
}
