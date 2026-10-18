/**
 * Stand-in for `$lib/utils/when-idle` that holds every callback until the test calls
 * `flushIdle`, so a test can assert what happens before the page goes idle and after.
 * Install with `vi.mock("$lib/utils/when-idle", () => import("$lib/test-stubs/when-idle"))`.
 */
const pending = new Set<() => void>();

export function whenIdle(callback: () => void): () => void {
  const entry = () => callback();
  pending.add(entry);
  return () => pending.delete(entry);
}

export function flushIdle(): void {
  const callbacks = [...pending];
  pending.clear();
  for (const callback of callbacks) callback();
}

export function resetIdle(): void {
  pending.clear();
}
