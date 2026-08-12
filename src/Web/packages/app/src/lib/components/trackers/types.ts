import type { NotificationUrgency } from "$api";

export interface TrackerNotification {
  id?: string; // For existing thresholds (from API)
  urgency: NotificationUrgency;
  hours: number | undefined;
  description?: string;
  displayOrder?: number;
  /** Managed alert rule delivering this threshold; absent until the server has synced one. */
  alertRuleId?: string;
}
