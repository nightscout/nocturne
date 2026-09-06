/**
 * Half-width of the window an inspection dialog searches for the record describing the instant it
 * was opened on. A loop cycle runs every five minutes, so this reaches at most one cycle either
 * side.
 */
const SEARCH_WINDOW_MS = 5 * 60 * 1000;

/**
 * Bounds of the window an inspection dialog searches. Paired with `limit: 1` and
 * `sort: "timestamp_desc"`, this keeps the latest record at or before the far edge of the window.
 */
export function inspectionSearchWindow(timestamp: Date): { from: Date; to: Date } {
  const centerMs = timestamp.getTime();
  return {
    from: new Date(centerMs - SEARCH_WINDOW_MS),
    to: new Date(centerMs + SEARCH_WINDOW_MS),
  };
}
