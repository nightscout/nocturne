/**
 * The signals a viewer starred and the ones they used last, kept in this browser only. A browser that refuses
 * storage keeps them for the page's life.
 */
import type { SignalId } from "./catalogue-vocabulary.js";

const KEY = "nocturne:report-signal-picks";

/** The starred signals before the viewer changes them. */
export const DEFAULT_STARRED: readonly SignalId[] = ["glucose", "target", "iob", "boluses", "carbs", "cob", "basal", "timeInRange"];

/** The most recently used signals kept. */
export const MAX_RECENT = 6;

interface Stored {
  starred: SignalId[];
  recent: SignalId[];
}

function strings(value: unknown): string[] | null {
  return Array.isArray(value) && value.every((v) => typeof v === "string") ? value : null;
}

function read(storage: Storage | null): Stored {
  try {
    const raw: unknown = JSON.parse(storage?.getItem(KEY) ?? "null");
    if (raw && typeof raw === "object")
      return {
        starred: strings(Reflect.get(raw, "starred")) ?? [...DEFAULT_STARRED],
        recent: strings(Reflect.get(raw, "recent")) ?? [],
      };
  } catch {
    // Unreadable storage holds no picks.
  }
  return { starred: [...DEFAULT_STARRED], recent: [] };
}

function browserStorage(): Storage | null {
  try {
    return typeof localStorage === "undefined" ? null : localStorage;
  } catch {
    return null;
  }
}

export class SignalPicks {
  starred = $state<SignalId[]>([]);
  recent = $state<SignalId[]>([]);
  readonly #storage: Storage | null;

  constructor(storage: Storage | null = browserStorage()) {
    this.#storage = storage;
    const stored = read(storage);
    this.starred = stored.starred;
    this.recent = stored.recent;
  }

  isStarred(id: SignalId): boolean {
    return this.starred.includes(id);
  }

  toggleStar(id: SignalId) {
    this.starred = this.isStarred(id) ? this.starred.filter((s) => s !== id) : [...this.starred, id];
    this.#save();
  }

  /** Puts the signal first among the recently used. */
  used(id: SignalId) {
    this.recent = [id, ...this.recent.filter((s) => s !== id)].slice(0, MAX_RECENT);
    this.#save();
  }

  #save() {
    try {
      this.#storage?.setItem(KEY, JSON.stringify({ starred: this.starred, recent: this.recent }));
    } catch {
      // A full or blocked storage keeps the picks for this page only.
    }
  }
}
