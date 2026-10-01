/**
 * Which treatment markers get a text label at the current zoom.
 *
 * Every bolus and carb marker carries its amount as a small label, and a meal
 * its name. At a phone's width a two-hour window puts five-minute SMBs about
 * 12px apart, so the labels paint over one another into a smear. This decides,
 * in pixel space, which labels fit: the largest amounts first, then anything
 * whose text does not overlap a label already placed. Glyphs are never hidden,
 * only their text; the tooltip and inspection dialogs still carry every value.
 */

export interface Interval {
  left: number;
  right: number;
}

export interface LabelCandidate<T> {
  item: T;
  /** Marker centre, in chart pixels. */
  x: number;
  text: string;
  /** Higher wins a contested spot. Ties fall to the leftmost. */
  priority: number;
}

/** Approximate advance of one glyph in the 8px amount labels. */
const AMOUNT_CHAR_WIDTH = 4.6;
/** Approximate advance of one glyph in the 7px meal-name labels. */
const NAME_CHAR_WIDTH = 4;
/** Approximate advance of one glyph on paper, where every label prints at 9px. */
export const PRINT_LABEL_CHAR_WIDTH = 5.2;
/** Breathing room between two neighbouring labels. */
const LABEL_GAP = 4;

function overlaps(a: Interval, b: Interval): boolean {
  return a.left < b.right && a.right > b.left;
}

function byPriorityThenX<T>(
  a: LabelCandidate<T>,
  b: LabelCandidate<T>
): number {
  return b.priority - a.priority || a.x - b.x;
}

/**
 * Place labels centred on their marker along one row. `obstacles` are spans the
 * row must also keep clear of, such as the track's own name.
 */
export function placeCenteredLabels<T>(
  candidates: readonly LabelCandidate<T>[],
  obstacles: readonly Interval[] = [],
  charWidth = AMOUNT_CHAR_WIDTH
): Set<T> {
  const placed: Interval[] = [...obstacles];
  const visible = new Set<T>();

  for (const c of [...candidates].sort(byPriorityThenX)) {
    const half = (c.text.length * charWidth + LABEL_GAP) / 2;
    const span = { left: c.x - half, right: c.x + half };
    if (placed.some((p) => overlaps(p, span))) continue;
    placed.push(span);
    visible.add(c.item);
  }

  return visible;
}

/**
 * Place meal names, which hang to the left of their marker's waist. A name must
 * clear every other marker's glyph column, or it would be painted over by a
 * neighbouring triangle, and every name already placed.
 *
 * @param glyphXs Centres of every treatment glyph in the row, in chart pixels.
 * @param halfWidth Half-width of a glyph; the name starts `halfWidth + gap`
 *   left of its own marker.
 * @param gap Space between the glyph and the name's trailing edge.
 */
export function placeTrailingLabels<T>(
  candidates: readonly LabelCandidate<T>[],
  glyphXs: readonly number[],
  halfWidth: number,
  gap: number,
  charWidth = NAME_CHAR_WIDTH
): Set<T> {
  const placed: Interval[] = [];
  const visible = new Set<T>();

  for (const c of [...candidates].sort(byPriorityThenX)) {
    const right = c.x - (halfWidth + gap);
    const span = { left: right - c.text.length * charWidth, right };

    // Its own glyph column ends at x - halfWidth, right of the name, so a
    // paired bolus at the same x is never counted as an obstacle here.
    const hitsGlyph = glyphXs.some((gx) =>
      overlaps({ left: gx - halfWidth, right: gx + halfWidth }, span)
    );
    if (hitsGlyph || placed.some((p) => overlaps(p, span))) continue;

    placed.push(span);
    visible.add(c.item);
  }

  return visible;
}
