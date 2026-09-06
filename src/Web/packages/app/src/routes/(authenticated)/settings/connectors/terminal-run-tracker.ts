import type { SyncProgressEvent } from "$lib/websocket/types";

/**
 * Tracks which connector runs have already been accounted for after finishing.
 *
 * A finished run's progress entry lingers on screen for a couple of seconds, during which the
 * other connectors in a batch keep emitting progress — so a reactive read of the whole progress
 * map fires repeatedly while one entry lingers. Asking this tracker instead answers "has anything
 * finished since I last looked", once per run rather than once per message.
 */
export function createTerminalRunTracker() {
  let seen = new Set<string>();

  return {
    /**
     * Whether any run has reached a terminal phase since the last call. Runs no longer present
     * are forgotten, so the set holds at most one entry per connector.
     */
    hasNewlyFinishedRun(
      progressByConnector: Record<string, SyncProgressEvent>,
    ): boolean {
      const finished = Object.values(progressByConnector)
        .filter((p) => p.phase === "Completed" || p.phase === "Failed")
        .map((p) => `${p.connectorId}@${p.timestamp}`);

      const isNew = finished.some((run) => !seen.has(run));
      seen = new Set(finished);
      return isNew;
    },
  };
}
