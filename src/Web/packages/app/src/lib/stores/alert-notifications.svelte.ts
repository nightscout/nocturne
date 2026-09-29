import type { InAppNotificationDto } from "$lib/api";
import { createSystemNotification } from "$lib/audio/alarm-sounds";
import { WebSocketClient } from "$lib/websocket/websocket-client.svelte";

/** The in-app notification type an alert delivered to this person's own account carries. */
const ALERT_FIRING = "alert.firing";

// eslint-disable-next-line svelte/prefer-svelte-reactivity -- bookkeeping, nothing renders from it
const shown = new Map<string, Notification>();

/**
 * Raises a newly created alert notification as a system notification: the
 * "this device" delivery of an alert. Called for a create only, so an alert
 * marked read or updated elsewhere is not raised again.
 */
export function raiseAlertNotification(notification: InAppNotificationDto): void {
  const id = notification.id;
  if (notification.type !== ALERT_FIRING || !id || shown.has(id)) return;
  const raised = createSystemNotification(
    notification.title ?? "",
    notification.subtitle ?? "",
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
    reconnectAttempts: Infinity,
    reconnectDelay: 5000,
    maxReconnectDelay: 30000,
    pingTimeout: 60000,
    pingInterval: 25000,
  });
  client.on("notificationCreated", raiseAlertNotification);
  client.on("notificationArchived", closeAlertNotification);
  client.connect();
  return () => client.destroy();
}
