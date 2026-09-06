import { getActiveAlerts } from "$api/generated/alerts.generated.remote";
import { pollWhileVisible } from "$lib/utils/poll-while-visible.svelte";

/** How often to re-read the active-alert surface while the tab is visible. */
const POLL_MS = 10_000;

/**
 * Keeps the shared `getActiveAlerts` query fresh.
 *
 * Call this exactly once, from the component that mounts the alert surfaces. Every
 * component that reads `getActiveAlerts().current` sees the refreshed data, so a
 * second caller would just double the request rate for the same result — which
 * is what happened when the banner and the fresh-fire toast each ran their own
 * timer at different cadences.
 */
export function pollActiveAlerts(): void {
  pollWhileVisible(() => getActiveAlerts().refresh(), POLL_MS);
}
