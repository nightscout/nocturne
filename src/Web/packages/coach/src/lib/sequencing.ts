import type { MarkState, MarkRegistration, SequenceConfig } from "./types.js";

export interface SelectionResult {
  key: string;
  step: number;
}

export function selectActiveMark(
  states: ReadonlyMap<string, MarkState>,
  registrations: MarkRegistration[],
  sequences: SequenceConfig,
): SelectionResult | null {
  const mountedMarkKeys = new Set(registrations.map((r) => r.key));

  // Check sequences by priority (highest first)
  const sortedSequences = Object.entries(sequences).sort(
    ([, a], [, b]) => b.priority - a.priority,
  );

  for (const [, seq] of sortedSequences) {
    if (seq.prerequisite && !isSequenceDone(seq.prerequisite, sequences, states)) {
      continue;
    }

    for (const stepKey of seq.steps) {
      if (!mountedMarkKeys.has(stepKey)) continue;

      const state = states.get(stepKey);
      if (state && state.status !== "unseen") continue;

      const stepRegistrations = registrations
        .filter((r) => r.key === stepKey)
        .sort((a, b) => a.step - b.step);

      if (stepRegistrations.length > 0) {
        return { key: stepKey, step: stepRegistrations[0].step };
      }
    }
  }

  // Check standalone marks
  const sequenceKeys = new Set(
    Object.values(sequences).flatMap((s) => s.steps),
  );

  const standaloneRegistrations = registrations
    .filter((r) => !sequenceKeys.has(r.key))
    .filter((r) => {
      const state = states.get(r.key);
      return !state || state.status === "unseen";
    })
    .sort((a, b) => b.priority - a.priority);

  if (standaloneRegistrations.length > 0) {
    return {
      key: standaloneRegistrations[0].key,
      step: standaloneRegistrations[0].step,
    };
  }

  return null;
}

export function isSequenceDone(
  seqName: string,
  sequences: SequenceConfig,
  states: ReadonlyMap<string, MarkState>,
): boolean {
  const seq = sequences[seqName];
  if (!seq) return true;

  return seq.steps.every((key) => {
    const state = states.get(key);
    return state && (state.status === "completed" || state.status === "dismissed");
  });
}

export function sequenceProgress(
  seqName: string,
  sequences: SequenceConfig,
  states: ReadonlyMap<string, MarkState>,
): { completed: number; total: number } {
  const seq = sequences[seqName];
  if (!seq) return { completed: 0, total: 0 };

  const completed = seq.steps.filter((key) => {
    const state = states.get(key);
    return state && (state.status === "completed" || state.status === "dismissed");
  }).length;

  return { completed, total: seq.steps.length };
}
