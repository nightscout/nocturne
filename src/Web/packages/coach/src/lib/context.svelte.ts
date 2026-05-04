import { getContext, setContext, untrack } from "svelte";
import type {
  CoachMarkAdapter,
  MarkRegistration,
  MarkState,
  MarkStatus,
  SequenceConfig,
} from "./types.js";
import { selectActiveMark, isSequenceDone, sequenceProgress, type SelectionResult } from "./sequencing.js";

const COACH_CONTEXT_KEY = Symbol("coach-mark-context");

export class CoachMarkContext {
  private adapter: CoachMarkAdapter;
  private sequences: SequenceConfig;
  private settleDelay: number;
  private seenDwellMs: number;
  private _keyToSequence: Map<string, string>;

  private _states = $state<Map<string, MarkState>>(new Map());
  private _registrations = $state<MarkRegistration[]>([]);
  private _activeSelection = $state<SelectionResult | null>(null);
  private _settleTimer: ReturnType<typeof setTimeout> | null = null;
  private _initialized = $state(false);

  private _forcedSequence = $state<string | null>(null);
  private _quietUntilNavigation = $state(false);

  activeKey = $derived(this._activeSelection?.key ?? null);
  activeStep = $derived(this._activeSelection?.step ?? null);

  constructor(
    adapter: CoachMarkAdapter,
    sequences: SequenceConfig = {},
    settleDelay = 500,
    seenDwellMs = 2000,
  ) {
    this.adapter = adapter;
    this.sequences = sequences;
    this.settleDelay = settleDelay;
    this.seenDwellMs = seenDwellMs;

    // Build O(1) lookup from mark key to sequence name
    this._keyToSequence = new Map();
    for (const [name, seq] of Object.entries(sequences)) {
      for (const step of seq.steps) {
        this._keyToSequence.set(step, name);
      }
    }
  }

  async initialize(): Promise<void> {
    const states = await this.adapter.fetchAll();
    const map = new Map<string, MarkState>();
    for (const s of states) {
      map.set(s.markKey, s);
    }
    this._states = map;
    this._initialized = true;
    this.scheduleSelection();
  }

  register(registration: MarkRegistration): () => void {
    this._registrations = untrack(() => [...this._registrations, registration]);
    this.scheduleSelection();

    // If a forced sequence is active and this key belongs to it, try advancing
    if (this._forcedSequence) {
      const seq = this.sequences[this._forcedSequence];
      if (seq && seq.steps.includes(registration.key) && !this._activeSelection) {
        this.activateNextForcedStep();
      }
    }

    return () => {
      this._registrations = untrack(() =>
        this._registrations.filter(
          (r) =>
            !(
              r.key === registration.key &&
              r.step === registration.step &&
              r.element === registration.element
            ),
        ),
      );
      this.scheduleSelection();
    };
  }

  activate(key: string, step: number): void {
    if (this._activeSelection && this._activeSelection.key !== key) {
      this.markSeen(this._activeSelection.key);
    }
    this._activeSelection = { key, step };
  }

  dismiss(key: string): void {
    if (this._forcedSequence) {
      // Dismissing any step in a forced sequence dismisses ALL remaining unseen/seen steps
      const seq = this.sequences[this._forcedSequence];
      if (seq) {
        for (const stepKey of seq.steps) {
          const status = this.getStatus(stepKey);
          if (status === "unseen" || status === "seen") {
            this.updateStatus(stepKey, "dismissed");
          }
        }
      }
      this._activeSelection = null;
      this.onForcedSequenceComplete();
      return;
    }

    this.updateStatus(key, "dismissed");
    this._activeSelection = null;
    this.scheduleSelection();
  }

  complete(key: string): void {
    this.updateStatus(key, "completed");
    if (this._activeSelection?.key === key) {
      this._activeSelection = null;
    }

    if (this._forcedSequence) {
      this.activateNextForcedStep();
    } else {
      this.scheduleSelection();
    }
  }

  markSeen(key: string): void {
    const state = this._states.get(key);
    if (!state || state.status === "unseen") {
      this.updateStatus(key, "seen");
    }
  }

  getStatus(key: string): MarkStatus {
    return this._states.get(key)?.status ?? "unseen";
  }

  isMarkEligible(key: string): boolean {
    const seqName = this._keyToSequence.get(key);
    if (!seqName) return true; // standalone marks are always eligible

    const seq = this.sequences[seqName];
    if (seq.prerequisite && !isSequenceDone(seq.prerequisite, this.sequences, this._states)) {
      return false;
    }

    return true;
  }

  getSequenceProgress(seqName: string): { completed: number; total: number } {
    return sequenceProgress(seqName, this.sequences, this._states);
  }

  getMountedSteps(key: string): MarkRegistration[] {
    return this._registrations
      .filter((r) => r.key === key)
      .sort((a, b) => a.step - b.step);
  }

  get seenDwell(): number {
    return this.seenDwellMs;
  }

  /** Force-activate a named sequence, overriding quiet mode if set. */
  startSequence(name: string): void {
    const seq = this.sequences[name];
    if (!seq) {
      console.warn(`[coach] Sequence "${name}" not found.`);
      return;
    }

    this._forcedSequence = name;
    // Intentionally overrides quiet mode — a new forced sequence always wins
    this._quietUntilNavigation = false;
    this._activeSelection = null;
    this.activateNextForcedStep();
  }

  clearQuiet(): void {
    this._quietUntilNavigation = false;
    this._forcedSequence = null;
    this.scheduleSelection();
  }

  async resetAll(): Promise<void> {
    if (this.adapter.deleteAll) {
      await this.adapter.deleteAll();
    }
    this._states = new Map();
    this._activeSelection = null;
    this._forcedSequence = null;
    this._quietUntilNavigation = false;
    this.scheduleSelection();
  }

  private activateNextForcedStep(): void {
    if (!this._forcedSequence) return;

    const seq = this.sequences[this._forcedSequence];
    if (!seq) return;

    const mountedKeys = new Set(this._registrations.map((r) => r.key));

    for (const stepKey of seq.steps) {
      const status = this.getStatus(stepKey);
      if (status === "completed" || status === "dismissed") continue;

      // Found the first unseen/seen step
      if (!mountedKeys.has(stepKey)) {
        // Not mounted yet — wait for lazy registration to trigger
        return;
      }

      // Mounted and eligible: activate it
      const stepRegistrations = this._registrations
        .filter((r) => r.key === stepKey)
        .sort((a, b) => a.step - b.step);

      if (stepRegistrations.length > 0) {
        this._activeSelection = { key: stepKey, step: stepRegistrations[0].step };
        return;
      }
    }

    // All steps done
    this.onForcedSequenceComplete();
  }

  private onForcedSequenceComplete(): void {
    if (!this._forcedSequence) return;

    const seq = this.sequences[this._forcedSequence];

    // Cross-complete keys from completesKeys
    if (seq?.completesKeys) {
      for (const key of seq.completesKeys) {
        const status = this.getStatus(key);
        if (status !== "completed" && status !== "dismissed") {
          this.updateStatus(key, "completed");
        }
      }
    }

    this._quietUntilNavigation = true;
    this._forcedSequence = null;
    this._activeSelection = null;
  }

  private updateStatus(key: string, status: MarkStatus): void {
    const existing = this._states.get(key);
    const now = new Date().toISOString();

    const updated: MarkState = {
      id: existing?.id ?? "",
      markKey: key,
      status,
      seenAt:
        status === "seen" && !existing?.seenAt ? now : (existing?.seenAt ?? null),
      completedAt:
        (status === "completed" || status === "dismissed") && !existing?.completedAt
          ? now
          : (existing?.completedAt ?? null),
    };

    const newMap = new Map(this._states);
    newMap.set(key, updated);
    this._states = newMap;

    // Fire and forget — optimistic
    this.adapter.update(key, status).catch(() => {});
  }

  private scheduleSelection(): void {
    if (!this._initialized) return;
    if (this._quietUntilNavigation) return;
    if (this._forcedSequence) return;
    if (this._settleTimer) clearTimeout(this._settleTimer);
    this._settleTimer = setTimeout(() => {
      if (this._activeSelection) return;
      this._activeSelection = selectActiveMark(
        this._states,
        this._registrations,
        this.sequences,
      );
    }, this.settleDelay);
  }
}

export function createCoachMarkContext(
  adapter: CoachMarkAdapter,
  sequences: SequenceConfig = {},
  settleDelay = 500,
  seenDwellMs = 2000,
): CoachMarkContext {
  const ctx = new CoachMarkContext(adapter, sequences, settleDelay, seenDwellMs);
  setContext(COACH_CONTEXT_KEY, ctx);
  return ctx;
}

export function getCoachMarkContext(): CoachMarkContext {
  return getContext<CoachMarkContext>(COACH_CONTEXT_KEY);
}
