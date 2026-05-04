import type { CoachMarkAdapter, MarkState, MarkStatus } from "@nocturne/coach";
import {
  getAll,
  updateStatus,
  deleteAll as deleteAllRemote,
} from "$lib/api/generated/coachMarks.generated.remote";

/**
 * Creates a {@link CoachMarkAdapter} backed by the generated remote functions.
 * Must be called within a Svelte component context (where remote functions are available).
 */
export function createCoachMarkAdapter(): CoachMarkAdapter {
  return {
    async fetchAll(): Promise<MarkState[]> {
      const states = await getAll();
      return (states ?? []).map((s) => ({
        id: s.id ?? "",
        markKey: s.markKey ?? "",
        status: (s.status as MarkStatus) ?? "unseen",
        seenAt: s.seenAt ? String(s.seenAt) : null,
        completedAt: s.completedAt ? String(s.completedAt) : null,
      }));
    },

    async update(key: string, status: MarkStatus): Promise<void> {
      await updateStatus({ key, request: { status } });
    },

    async deleteAll(): Promise<void> {
      await deleteAllRemote();
    },
  };
}
