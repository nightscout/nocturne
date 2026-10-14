/**
 * How the artwork is presented on this device.
 *
 * `animated` plays each reveal and still honours the OS reduced-motion
 * setting, `still` draws the finished painting without simulating anything,
 * and `off` draws nothing at all.
 */
export type Presentation = 'animated' | 'still' | 'off';

export const PRESENTATIONS: readonly Presentation[] = ['animated', 'still', 'off'];

let current: Presentation = 'animated';
const listeners = new Set<(value: Presentation) => void>();

export function getPresentation(): Presentation {
  return current;
}

export function setPresentation(value: Presentation): void {
  if (value === current) return;
  current = value;
  for (const listener of [...listeners]) listener(value);
}

/** Calls back on each change, not on subscribing. Returns the unsubscribe. */
export function subscribePresentation(listener: (value: Presentation) => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}
