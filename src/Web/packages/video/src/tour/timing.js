/** Seconds. The build reads this too, to write each composition's `data-duration`. */
export const INTRO = 3.6;
export const CAMERA = 1.4;
export const TRAVEL = 1.1;
export const SCRUB = 3.2;
/** After the pull back starts, when the logo begins; it then runs its course and holds. */
export const LOGO_IN = 1.4;
/** The logo runs `LOGO_DURATION` (src/logo), which this module cannot import: the build reads it in Node. */
export const OUTRO = LOGO_IN + 6;

/** How long a click or a choice takes on screen, and how fast typed text appears, per character. */
export const CLICK = 0.5;
export const KEY = 0.06;

/** Long enough to read a note at a relaxed pace, with a moment to look at the chart. */
export const readTime = (text) => Math.max(3.4, 1.4 + text.trim().split(/\s+/).length * 0.3);

/** When each of a beat's actions starts, counted from the moment the cursor arrives, and how long they all take. */
export function actionTimes(actions = []) {
  let t = 0;
  const starts = actions.map((action) => {
    const start = t;
    t += action.type !== undefined ? action.text.length * KEY + 0.3 : CLICK;
    return start;
  });
  return { starts, length: t };
}

/**
 * When each scene and beat of a film starts and ends. A beat is the cursor travelling to its first
 * point, then scrubbing its path, resting, or acting, for as long as its note takes to read.
 */
export function schedule({ scenes }) {
  let t = INTRO;
  const planned = scenes.map((scene) => {
    const start = t;
    let at = start + CAMERA;
    const beats = scene.beats.map((beat) => {
      const travelStart = at;
      const arrive = at + TRAVEL;
      const scrub = beat.path ? SCRUB : 0;
      const acting = actionTimes(beat.do).length;
      const end = arrive + Math.max(scrub + 0.8, acting ? acting + 1.2 : 0, readTime(beat.note));
      at = end;
      return { travelStart, arrive, scrubEnd: arrive + scrub, end };
    });
    t = at + 0.3;
    return { start, beats, end: t };
  });
  return { planned, outro: t, total: Math.round((t + OUTRO) * 100) / 100 };
}
