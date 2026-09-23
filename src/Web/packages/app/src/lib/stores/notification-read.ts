/** The notifications with each unread one that `match` accepts marked read now. */
export function markedRead<T extends { readAt?: Date }>(
  notifications: readonly T[],
  match: (notification: T) => boolean = () => true
): T[] {
  const readAt = new Date();
  return notifications.map((n) => (!n.readAt && match(n) ? { ...n, readAt } : n));
}
