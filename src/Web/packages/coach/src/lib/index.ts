// @nocturne/coach — Progressive disclosure system
export type {
  CoachMarkAdapter,
  CoachMarkOptions,
  CoachMarkProviderOptions,
  CoachMarkStep,
  DismissOptions,
  MarkRegistration,
  MarkState,
  MarkStatus,
  SequenceConfig,
  SequenceDefinition,
} from "./types.js";

export { default as CoachMarkProvider } from "./CoachMarkProvider.svelte";
export { coachmark } from "./coachmark.svelte.js";
export {
  CoachMarkContext,
  getCoachMarkContext,
  createCoachMarkContext,
} from "./context.svelte.js";
export { selectActiveMark, isSequenceDone, sequenceProgress } from "./sequencing.js";
export type { SelectionResult } from "./sequencing.js";
