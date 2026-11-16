/**
 * A report's unsaved changes, kept in this browser so a reload or a navigation does not lose them:
 * one draft for a new report and one for each saved report, by id. Browser storage is per origin,
 * so per tenant. It can be missing, or refuse a read or a write (a private window, blocked or full
 * storage); then nothing is kept, and the workspace works as before.
 */
import { BUILDER_VERSION } from "./saved-state.js";
import type { Report } from "./report.svelte.js";

const PREFIX = "nocturne.custom-report-draft.";

/** The draft key of a report not yet saved. */
export const NEW_REPORT = "new";

/** A report's state as the workspace saves it with the spec, and that state's version. */
export interface Draft {
  version: number;
  state: unknown;
}

function storageKey(id: string): string {
  return PREFIX + id;
}

export function readDraft(id: string): Draft | null {
  try {
    const raw = localStorage.getItem(storageKey(id));
    if (!raw) return null;
    const parsed: unknown = JSON.parse(raw);
    if (typeof parsed !== "object" || parsed === null || !("version" in parsed) || !("state" in parsed)) return null;
    return typeof parsed.version === "number" ? { version: parsed.version, state: parsed.state } : null;
  } catch {
    return null;
  }
}

export function writeDraft(id: string, text: string): void {
  try {
    localStorage.setItem(storageKey(id), `{"version":${BUILDER_VERSION},"state":${text}}`);
  } catch {
    // Storage refused the write; the draft is not kept.
  }
}

export function clearDraft(id: string): void {
  try {
    localStorage.removeItem(storageKey(id));
  } catch {
    // Storage cannot be read; there is no draft to clear.
  }
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
  /** The report's state as it opened, or null when it opened from a draft. */
  #baseline: string | null = null;
  /** Whether the report on screen differs from the state it opened with. */
  dirty = $state(false);
  /** Whether the report on screen opened from a draft. */
  restored = $state(false);

  constructor(id: string) {
    this.id = id;
  }

  /**
   * The report to show: `opened`, or the report this browser's draft describes when there is one that
   * differs from it. `fromDraft` makes a report from a draft, or null when the draft no longer reads.
   * With no `opened` (a new report), the draft's report, or null.
   */
  open(opened: Report | null, fromDraft: (draft: Draft) => Report | null): Report | null {
    this.#baseline = opened ? stateText(opened) : null;
    const draft = readDraft(this.id);
    const restored = draft ? fromDraft(draft) : null;
    if (draft && !restored) clearDraft(this.id);
    if (restored && stateText(restored) !== this.#baseline) {
      this.restored = true;
      this.dirty = true;
      return restored;
    }
    this.restored = false;
    this.dirty = false;
    return opened;
  }

  /** Starts the draft of a report that did not open from one: its current state is the baseline. */
  begin(report: Report): void {
    this.#baseline = stateText(report);
    this.restored = false;
    this.dirty = false;
  }

  /** Keeps the report's state as the draft while it differs from the baseline, and clears it when not. */
  keep(report: Report): void {
    const text = stateText(report);
    const dirty = text !== this.#baseline;
    if (dirty) writeDraft(this.id, text);
    else clearDraft(this.id);
    this.dirty = dirty;
  }

  /** The report was saved, or its changes discarded: nothing is kept, and leaving loses nothing. */
  clear(): void {
    clearDraft(this.id);
    this.dirty = false;
    this.restored = false;
  }
}
