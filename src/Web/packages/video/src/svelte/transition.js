/**
 * `svelte/transition` for a film. A JS transition runs on the browser's clock, which a render does
 * not advance between frames, so an element would stay part way in or out. Here each one finishes
 * at once.
 */
const none = () => ({ duration: 0 });

export const blur = none;
export const draw = none;
export const fade = none;
export const fly = none;
export const scale = none;
export const slide = none;
export const crossfade = () => [none, none];
