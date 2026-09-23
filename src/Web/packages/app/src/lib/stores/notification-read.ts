import { isoNow } from "$lib/utils/now";

/** The notifications with each unread one that `match` accepts marked read now. */
export function markedRead<T extends { readAt?: string }>(
  notifications: readonly T[],
  match: (notification: T) => boolean = () => true
): T[] {
  const readAt = isoNow();
  return notifications.map((n) => (!n.readAt && match(n) ? { ...n, readAt } : n));
}
