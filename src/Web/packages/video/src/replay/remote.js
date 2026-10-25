import { parse } from "devalue";
import { argKey } from "./arg-key.js";
import { remoteCommand, remoteForm, remoteQuery } from "$lib/test-stubs/remote-resource";
import { phase } from "./phase.svelte.js";

const decode = (set = {}) => ({
  results: Object.fromEntries(Object.entries(set.results ?? {}).map(([name, encoded]) => [name, parse(encoded)])),
  calls: Object.fromEntries(Object.entries(set.calls ?? {}).map(([name, list]) => [name, list.map((encoded) => parse(encoded))])),
  args: set.args ?? {},
});

/** What the app answered before any action, then what it answered after each one, in order. */
let sets = [decode()];
const seen = {};

/** Loads the recorded results the page's queries answer with, from before any action. */
export function useFixture(fixture) {
  sets = [decode(fixture), ...(fixture.phases ?? []).map(decode)];
  phase.n = 0;
}

/** Moves replay to the app as it was after `n` of the film's actions. */
export function setPhase(n) {
  if (phase.n !== n) phase.n = n;
}

/**
 * A query answers with what it returned when the fixture was captured, at the film's current
 * phase: the latest answer at or before it. Most functions are called with one argument and answer
 * the same whatever it is. One the page called with several (a comparison's two periods) answers
 * with what was captured for the same argument. A fixture captured before arguments were recorded
 * answers by order: the nth distinct argument first asked in a phase gets that phase's nth answer,
 * since the page asks in the same order on every run. One the capture never saw stays pending, so the component shows
 * its loading state rather than an error.
 */
export function replayQuery(name) {
  return (arg) => {
    if (!sets.some((set) => name in set.results || name in set.calls)) {
      console.warn(`[replay] no fixture for ${name}`);
      return new Promise(() => {});
    }
    seen[name] ??= new Map();
    const key = argKey(arg);
    if (!seen[name].has(key)) {
      const first = Math.min(phase.n, sets.length - 1);
      const order = [...seen[name].values()].filter((asked) => asked.phase === first).length;
      seen[name].set(key, { phase: first, order });
    }
    return remoteQuery(() => answer(name, key, seen[name].get(key)));
  };
}

/** An argument first asked in a later phase than one holding several answers is not among them. */
function answer(name, key, asked) {
  for (let k = Math.min(phase.n, sets.length - 1); k >= 0; k--) {
    const { results, calls, args } = sets[k];
    if (args[name]) {
      const at = args[name].indexOf(key);
      if (at >= 0) return calls[name][at];
      continue;
    }
    if (name in calls && k <= asked.phase) return calls[name][Math.min(asked.order, calls[name].length - 1)];
    if (name in calls && k > asked.phase) continue;
    if (name in results) return results[name];
  }
  return undefined;
}

/**
 * A command answers with what it returned when the fixture was captured: what the action that ran it
 * got back. A command the capture never saw answers with nothing.
 */
export function replayCommand(name) {
  return remoteCommand(() => {
    for (let k = Math.min(phase.n, sets.length - 1); k >= 0; k--) {
      if (name in sets[k].results) return sets[k].results[name];
    }
    return undefined;
  });
}
export const replayForm = () => remoteForm();
