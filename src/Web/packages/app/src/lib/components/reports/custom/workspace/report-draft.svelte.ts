/**
 * A report's unsaved changes, kept in this browser so a reload or a navigation does not lose them:
 * one draft for a new report and one for each saved report, by id, for each member of each tenant.
 * A draft holds what the report shows (titles, foods, dates, thresholds), so it is dropped when the
 * member signs out (`clearAllDrafts`), when its report is deleted, and once it is
 * `DRAFT_MAX_AGE_MS` old. Browser storage can be missing, or refuse a read or a write (a private
 * window, blocked or full storage); then nothing is kept, the workspace works as before, and
 * `ReportDraft.kept` says so.
 */
import { BUILDER_VERSION } from "./saved-state.js";
import type { Report } from "./report.svelte.js";

const PREFIX = "nocturne.custom-report-draft.";

/** The draft key of a report not yet saved. */
export const NEW_REPORT = "new";

/** How long a draft is kept after its last change. */
export const DRAFT_MAX_AGE_MS = 14 * 24 * 60 * 60 * 1000;

/** Whose drafts these are: the tenant's slug and the member's subject id. */
export interface DraftOwner {
  tenant: string;
  subject: string;
}

/** The owner of the drafts read and written for the page's signed-in member. */
export function draftOwner(data: Record<string, unknown>): DraftOwner {
  const tenant = data.tenantSlug;
  const user = data.user;
  const subject = user && typeof user === "object" && "subjectId" in user ? user.subjectId : null;
  return { tenant: typeof tenant === "string" ? tenant : "", subject: typeof subject === "string" ? subject : "" };
}

/**
 * A report's state as the workspace saves it with the spec, that state's version, the `updatedAt`
 * of the saved report the changes were made to (null for a new report), and when it was written.
 */
export interface Draft {
  version: number;
  state: unknown;
  savedAt: string | null;
  writtenAt: number;
}

function storageKey(owner: DraftOwner, id: string): string {
  return `${PREFIX}${owner.tenant}.${owner.subject}.${id}`;
}

function parse(raw: string | null): Draft | null {
  if (!raw) return null;
  try {
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== "object" || parsed === null) return null;
    const [version, state, savedAt, writtenAt]: unknown[] = ["version", "state", "savedAt", "writtenAt"].map((k) => Reflect.get(parsed, k));
    if (typeof version !== "number" || state === undefined || typeof writtenAt !== "number") return null;
    if (savedAt !== null && typeof savedAt !== "string") return null;
    return { version, state, savedAt, writtenAt };
  } catch {
    return null;
  }
}

export function readDraft(owner: DraftOwner, id: string): Draft | null {
  try {
    return parse(localStorage.getItem(storageKey(owner, id)));
  } catch {
    return null;
  }
}

/** Keeps `text` as the draft of report `id`; false when storage refused the write. */
export function writeDraft(owner: DraftOwner, id: string, text: string, savedAt: string | null, now = Date.now()): boolean {
  try {
    const record = `{"version":${BUILDER_VERSION},"savedAt":${JSON.stringify(savedAt)},"writtenAt":${now},"state":${text}}`;
    localStorage.setItem(storageKey(owner, id), record);
    return true;
  } catch {
    return false;
  }
}

export function clearDraft(owner: DraftOwner, id: string): void {
  try {
    localStorage.removeItem(storageKey(owner, id));
  } catch {
    // Storage cannot be read; there is no draft to clear.
  }
}

/** Removes the draft keys `drop` selects, whoever they belong to. */
function sweep(drop: (raw: string | null) => boolean): void {
  try {
    const keys: string[] = [];
    for (let i = 0; i < localStorage.length; i++) {
      const key = localStorage.key(i);
      if (key?.startsWith(PREFIX)) keys.push(key);
    }
    for (const key of keys) if (drop(localStorage.getItem(key))) localStorage.removeItem(key);
  } catch {
    // Storage cannot be read; there are no drafts to clear.
  }
}

/** Removes every draft in this browser, of every member and tenant: done when a session ends. */
export function clearAllDrafts(): void {
  sweep(() => true);
}

/** Removes every draft older than `DRAFT_MAX_AGE_MS`, and every draft that no longer reads. */
function dropExpiredDrafts(now = Date.now()): void {
  sweep((raw) => {
    const draft = parse(raw);
    return !draft || now - draft.writtenAt > DRAFT_MAX_AGE_MS;
  });
}

/** The report's saved state as text, the form two states are compared in. */
export function stateText(report: Report): string {
  return JSON.stringify(report.toState());
}

/**
 * The draft of one report: whether the report differs from the state it opened with, and that
 * difference kept in storage while it does.
 */
export class ReportDraft {
  readonly id: string;
  readonly owner: DraftOwner;
  /** The report's state as it opened, or null when it opened from a draft. */
  #baseline: string | null = null;
  /** The `updatedAt` of the saved report the workspace opened, null for a new report. */
  #savedAt: string | null = null;
  /** Whether the report on screen differs from the state it opened with. */
  dirty = $state(false);
  /** Whether the report on screen opened from a draft. */
  restored = $state(false);
  /** Whether this browser keeps the unsaved changes: false after storage refused the last write. */
  kept = $state(true);
  /**
   * The report a draft describes when the saved report changed after the draft was made: it opens
   * only when the member keeps it (`keepStale`), else `discardStale` drops it.
   */
  stale = $state<Report | null>(null);

  constructor(id: string, owner: DraftOwner) {
    this.id = id;
    this.owner = owner;
  }

  /**
   * The report to show: `opened`, or the report this browser's draft describes when there is one
   * that differs from it and was made to the same saved version (`savedAt`). A draft made to an
   * older version is held in `stale` and `opened` is shown. `fromDraft` makes a report from a draft,
   * or null when the draft no longer reads. With no `opened` (a new report), the draft's report, or
   * null.
   */
  open(opened: Report | null, savedAt: string | null, fromDraft: (draft: Draft) => Report | null): Report | null {
    dropExpiredDrafts();
    this.#baseline = opened ? stateText(opened) : null;
    this.#savedAt = savedAt;
    this.stale = null;
    this.kept = true;
    const draft = readDraft(this.owner, this.id);
    const restored = draft ? fromDraft(draft) : null;
    if (draft && !restored) clearDraft(this.owner, this.id);
    this.restored = false;
    this.dirty = false;
    if (!draft || !restored || stateText(restored) === this.#baseline) return opened;
    if (opened && draft.savedAt !== savedAt) {
      this.stale = restored;
      return opened;
    }
    this.restored = true;
    this.dirty = true;
    return restored;
  }

  /** Opens the stale draft's report over the saved report as it is now; it counts as unsaved. */
  keepStale(): Report | null {
    const report = this.stale;
    if (!report) return null;
    this.stale = null;
    this.restored = true;
    this.dirty = true;
    this.kept = writeDraft(this.owner, this.id, stateText(report), this.#savedAt);
    return report;
  }

  /** Drops the stale draft: the saved report shows as it is now. */
  discardStale(): void {
    this.clear();
  }

  /** Starts the draft of a report that did not open from one: its current state is the baseline. */
  begin(report: Report): void {
    this.#baseline = stateText(report);
    this.restored = false;
    this.dirty = false;
    this.kept = true;
  }

  /**
   * Keeps the report's state as the draft while it differs from the baseline, and clears it when
   * not. Does nothing while a stale draft waits for the member's choice, which would overwrite it.
   */
  keep(report: Report): void {
    if (this.stale) return;
    const text = stateText(report);
    const dirty = text !== this.#baseline;
    if (dirty) this.kept = writeDraft(this.owner, this.id, text, this.#savedAt);
    else {
      clearDraft(this.owner, this.id);
      this.kept = true;
    }
    this.dirty = dirty;
  }

  /** The report was saved, or its changes discarded: nothing is kept, and leaving loses nothing. */
  clear(): void {
    clearDraft(this.owner, this.id);
    this.stale = null;
    this.dirty = false;
    this.restored = false;
    this.kept = true;
  }
}
