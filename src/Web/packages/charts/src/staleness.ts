/**
 * Threshold past which a glucose reading is treated as stale: 10 minutes.
 * Above this, the dashboard greys/dims the value, hides the trend chevron,
 * and signals "no recent data" to dependent components.
 */
export const STALE_THRESHOLD_MS = 10 * 60 * 1000;
