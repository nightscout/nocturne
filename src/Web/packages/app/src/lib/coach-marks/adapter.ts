import type { CoachMarkAdapter, MarkState, MarkStatus } from "@nocturne/coach";
import {
  getAll,
  updateStatus,
  deleteAll as deleteAllRemote,
} from "$lib/api/generated/coachMarks.generated.remote";

const LOCAL_STORAGE_KEY = "nocturne:coach-marks";

function readLocal(): Map<string, MarkState> {
  try {
    const raw = localStorage.getItem(LOCAL_STORAGE_KEY);
    if (!raw) return new Map();
    const arr: MarkState[] = JSON.parse(raw);
    return new Map(arr.map((s) => [s.markKey, s]));
  } catch {
    return new Map();
  }
}

function writeLocal(states: Map<string, MarkState>): void {
  try {
    localStorage.setItem(LOCAL_STORAGE_KEY, JSON.stringify([...states.values()]));
  } catch {
    // storage full or unavailable — silently ignore
  }
}

function isAuthError(err: unknown): boolean {
  return (
    typeof err === "object" &&
    err !== null &&
    "status" in err &&
    (err as { status: number }).status === 401
  );
}

/**
 * Creates a {@link CoachMarkAdapter} backed by the generated remote functions,
 * with localStorage fallback when unauthenticated.
 */
export function createCoachMarkAdapter(): CoachMarkAdapter {
  let usingLocal = false;

  return {
    async fetchAll(): Promise<MarkState[]> {
      try {
        const states = await getAll();
        // Authenticated — merge any localStorage marks into the server response
        // then clear local storage so we don't drift.
        const local = readLocal();
        if (local.size > 0) {
          localStorage.removeItem(LOCAL_STORAGE_KEY);
        }

        const remote = (states ?? []).map(
          (s): MarkState => ({
            id: s.id ?? "",
            markKey: s.markKey ?? "",
            status: (s.status as MarkStatus) ?? "unseen",
            seenAt: s.seenAt ? String(s.seenAt) : null,
            completedAt: s.completedAt ? String(s.completedAt) : null,
          }),
        );

        // If there were local-only marks, push them to the server
        const remoteKeys = new Set(remote.map((s) => s.markKey));
        for (const [key, state] of local) {
          if (!remoteKeys.has(key) && state.status !== "unseen") {
            updateStatus({ key, request: { status: state.status } }).catch(() => {});
            remote.push(state);
          }
        }

        return remote;
      } catch (err) {
        if (isAuthError(err)) {
          usingLocal = true;
          return [...readLocal().values()];
        }
        throw err;
      }
    },

    async update(key: string, status: MarkStatus): Promise<void> {
      if (usingLocal) {
        const states = readLocal();
        const existing = states.get(key);
        const now = new Date().toISOString();
        states.set(key, {
          id: existing?.id ?? "",
          markKey: key,
          status,
          seenAt:
            status === "seen" && !existing?.seenAt
              ? now
              : (existing?.seenAt ?? null),
          completedAt:
            status === "completed" || status === "dismissed"
              ? existing?.completedAt ?? now
              : (existing?.completedAt ?? null),
        });
        writeLocal(states);
        return;
      }

      try {
        await updateStatus({ key, request: { status } });
      } catch (err) {
        if (isAuthError(err)) {
          usingLocal = true;
          // Retry via local path
          await this.update(key, status);
          return;
        }
        throw err;
      }
    },

    async deleteAll(): Promise<void> {
      if (usingLocal) {
        localStorage.removeItem(LOCAL_STORAGE_KEY);
        return;
      }

      try {
        await deleteAllRemote();
      } catch (err) {
        if (isAuthError(err)) {
          usingLocal = true;
          localStorage.removeItem(LOCAL_STORAGE_KEY);
          return;
        }
        throw err;
      }
    },
  };
}
