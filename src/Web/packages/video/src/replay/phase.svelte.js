/**
 * How many of the film's actions have run. Replay answers as the capture saw the app at that same
 * point, and every query reads this, so a page re-renders with the after-state when an action that
 * changed the data (a save) has run.
 */
export const phase = $state({ n: 0 });
