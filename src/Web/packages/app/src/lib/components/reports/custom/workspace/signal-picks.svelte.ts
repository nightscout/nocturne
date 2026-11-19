/**
 * The signals a member starred and the ones they used last, kept in this browser only, for each
 * member of each tenant, and cleared with the rest of what the browser keeps for them (see
 * `member-storage.ts`). A browser that refuses storage, or a page with no one member to keep them
 * for, keeps them for the page's life.
 */
import { memberKey, type MemberOwner } from "#lib/stores/member-storage.js";
import type { SignalId } from "./catalogue-vocabulary.js";

const NAME = "report-signal-picks";

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

function read(storage: Storage | null, key: string | null): Stored {
  try {
    const raw: unknown = JSON.parse((key && storage?.getItem(key)) ?? "null");
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
  readonly #key: string | null;

  constructor(owner: MemberOwner, storage: Storage | null = browserStorage()) {
    this.#storage = storage;
    this.#key = memberKey(owner, NAME);
    const stored = read(storage, this.#key);
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
      if (this.#key) this.#storage?.setItem(this.#key, JSON.stringify({ starred: this.starred, recent: this.recent }));
    } catch {
      // A full or blocked storage keeps the picks for this page only.
    }
  }
}
