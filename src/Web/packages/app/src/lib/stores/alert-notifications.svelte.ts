import { InAppNotificationType, type InAppNotificationDto } from "@nocturne/api-client";
import { createSystemNotification } from "#lib/audio/alarm-sounds.js";
import { resolveNotificationLabel } from "#lib/utils/notification-labels.js";
import { WebSocketClient } from "#lib/websocket/websocket-client.svelte.js";
import { BROWSER_SOCKET_TIMING } from "#lib/websocket/socket-options.js";

/**
 * The in-app notification types an alert rule delivers to this person's own account: an alert,
 * the notice that the rule cannot run until its conditions are edited, and the notice that it has
 * waited 24 hours for missing data.
 */
const ALERT_TYPES = new Set<string>([
  InAppNotificationType.AlertFiring,
  InAppNotificationType.AlertUnevaluable,
  InAppNotificationType.AlertHeld,
]);

// eslint-disable-next-line svelte/prefer-svelte-reactivity -- bookkeeping, nothing renders from it
const shown = new Map<string, Notification>();

/**
 * Raises a newly created alert notification as a system notification: the
 * "this device" delivery of an alert. Called for a create only, so an alert
 * marked read or updated elsewhere is not raised again.
 */
export function raiseAlertNotification(notification: InAppNotificationDto): void {
  const id = notification.id;
  if (!ALERT_TYPES.has(notification.type ?? "") || !id || shown.has(id)) return;
  const raised = createSystemNotification(
    notification.title ?? "",
    resolveNotificationLabel(notification.subtitle),
    `alert-${id}`,
    false
  );
  if (raised) shown.set(id, raised);
}

/** Closes the system notification of an alert that was resolved, acknowledged or dismissed. */
export function closeAlertNotification(notification: InAppNotificationDto): void {
  if (!notification.id) return;
  shown.get(notification.id)?.close();
  shown.delete(notification.id);
}

/**
 * Listens for alerts on this person's account on a page outside the
 * authenticated layout, whose realtime store does this everywhere else, so a
 * test alert reaches the page the way a real one reaches the app. Returns the
 * function that stops listening.
 */
export function listenForAlertNotifications(): () => void {
  const client = new WebSocketClient({
    url: window.location.origin,
    ...BROWSER_SOCKET_TIMING,
  });
  client.on("notificationCreated", raiseAlertNotification);
  client.on("notificationArchived", closeAlertNotification);
  client.connect();
  return () => client.destroy();
}
