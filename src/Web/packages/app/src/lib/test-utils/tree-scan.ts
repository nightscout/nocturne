/**
 * Budget for a guard that reads the whole source tree. The default five seconds
 * is sized for logic tests; a cold file cache, or a full parallel run, can put
 * these reads an order of magnitude above it.
 */
export const TREE_SCAN_TIMEOUT_MS = 60_000;
