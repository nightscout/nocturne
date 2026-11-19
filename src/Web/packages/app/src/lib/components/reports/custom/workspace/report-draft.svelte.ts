/**
 * A report's unsaved changes, kept in this browser so a reload or a navigation does not lose them:
 * one draft for a new report and one for each saved report, by id, for each member of each tenant.
 * A draft holds what the report shows (titles, foods, dates, thresholds), so it is dropped when the
 * member's session ends (see `member-storage.ts`), when its report is deleted or found gone, and once
 * it is `DRAFT_MAX_AGE_MS` old. Browser storage can be missing, or refuse a read or a write (a private
 * window, blocked or full storage); then nothing is kept, the workspace works as before, and
 * `ReportDraft.kept` says so. A page with no one member to keep it for (a guest session, no subject)
 * keeps nothing either.
 */
import { clearMemberKeys, memberKey, type MemberOwner } from "#lib/stores/member-storage.js";
import { BUILDER_VERSION } from "./saved-state.js";
import type { Report } from "./report.svelte.js";

const NAME = "custom-report-draft";

/** The draft key of a report not yet saved. */
export const NEW_REPORT = "new";

/** How long a draft is kept after its last change. */
export const DRAFT_MAX_AGE_MS = 14 * 24 * 60 * 60 * 1000;

/** Whose drafts these are: the tenant's slug and the member's subject id. */
export type DraftOwner = MemberOwner;

export { memberOwner as draftOwner } from "#lib/stores/member-storage.js";

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

/** The draft's key, or null when the owner is not one member, who keeps no drafts. */
export function draftKey(owner: DraftOwner, id: string): string | null {
  const key = memberKey(owner, NAME);
  return key && `${key}.${id}`;
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
  const key = draftKey(owner, id);
  if (!key) return null;
  try {
    return parse(localStorage.getItem(key));
  } catch {
    return null;
  }
}

/** Keeps `text` as the draft of report `id`; false when storage refused the write or there is no one to keep it for. */
export function writeDraft(owner: DraftOwner, id: string, text: string, savedAt: string | null, now = Date.now()): boolean {
  const key = draftKey(owner, id);
  if (!key) return false;
  try {
    const record = `{"version":${BUILDER_VERSION},"savedAt":${JSON.stringify(savedAt)},"writtenAt":${now},"state":${text}}`;
    localStorage.setItem(key, record);
    return true;
  } catch {
    return false;
  }
}

/**
 * Marks the draft of report `id`, made to the saved version `from`, as made to `to`: for a write that
 * changed the report but not what the draft changes (a rename), so the draft does not read as made
 * to an older version.
 */
export function moveDraftVersion(owner: DraftOwner, id: string, from: string | null, to: string | null): void {
  const draft = readDraft(owner, id);
  if (!draft || draft.savedAt !== from || from === to) return;
  writeDraft(owner, id, JSON.stringify(draft.state), to, draft.writtenAt);
}

export function clearDraft(owner: DraftOwner, id: string): void {
  const key = draftKey(owner, id);
  if (!key) return;
  try {
    localStorage.removeItem(key);
  } catch {
    // Storage cannot be read; there is no draft to clear.
  }
}

/** Removes every draft in this browser, of every member and tenant. */
export function clearAllDrafts(): void {
  clearMemberKeys(`${NAME}.`);
}

/** Removes every draft older than `DRAFT_MAX_AGE_MS`, and every draft that no longer reads. */
export function dropExpiredDrafts(now = Date.now()): void {
  clearMemberKeys(`${NAME}.`, (raw) => {
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
  /**
   * Whether this browser keeps the unsaved changes: false after storage refused the last write, and
   * always for a page with no one member to keep them for.
   */
  kept = $state(true);
  /**
   * The report a draft describes when the saved report changed after the draft was made: it opens
   * only when the member keeps it (`keepStale`), else `discardStale` drops it.
   */
  stale = $state<Report | null>(null);

  readonly #keeps: boolean;

  /** Whether there is one member to keep the draft for (see `draftKey`). */
  get keeps(): boolean {
    return this.#keeps;
  }

  constructor(id: string, owner: DraftOwner) {
    this.id = id;
    this.owner = owner;
    this.#keeps = draftKey(owner, id) !== null;
    this.kept = this.#keeps;
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
    this.kept = this.#keeps;
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
    this.kept = this.#keeps;
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
      this.kept = this.#keeps;
    }
    this.dirty = dirty;
  }

  /** The report was saved, or its changes discarded: nothing is kept, and leaving loses nothing. */
  clear(): void {
    clearDraft(this.owner, this.id);
    this.stale = null;
    this.dirty = false;
    this.restored = false;
    this.kept = this.#keeps;
  }
}
