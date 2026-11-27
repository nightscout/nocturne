/** The most flagged people the wall's top band holds. Any more are listed in the columns. */
export const WALL_BAND_MAX = 6;

/** The share of the screen's height the top band may take. */
const BAND_HEIGHT_SHARE = 0.4;

/**
 * One size of type for the wall, chosen by the width of the screen. The row height is kept next
 * to the classes that set it, so the number the capacity is measured with is the height the rows
 * have. Larger screens step up in type before they gain columns.
 */
export interface WallStep {
  minWidth: number;
  /** Height of a column line, in px; `rowClass` sets it. */
  rowPx: number;
  rowClass: string;
  nameClass: string;
  valueSize: "xs" | "sm" | "lg";
  arrowPx: number;
  columnPx: number;
  /** The height of a flagged card with two reasons, in px. */
  cardPx: number;
  cardNameClass: string;
  cardSize: "lg" | "xl" | "2xl";
  footerClass: string;
  clockClass: string;
}

export const WALL_STEPS: readonly WallStep[] = [
  {
    minWidth: 0,
    rowPx: 36,
    rowClass: "h-9",
    nameClass: "text-base",
    valueSize: "xs",
    arrowPx: 16,
    columnPx: 300,
    cardPx: 108,
    cardNameClass: "text-xl",
    cardSize: "lg",
    footerClass: "text-sm",
    clockClass: "text-lg",
  },
  {
    minWidth: 1600,
    rowPx: 52,
    rowClass: "h-13",
    nameClass: "text-xl",
    valueSize: "sm",
    arrowPx: 24,
    columnPx: 440,
    cardPx: 150,
    cardNameClass: "text-3xl",
    cardSize: "xl",
    footerClass: "text-lg",
    clockClass: "text-2xl",
  },
  {
    minWidth: 2800,
    rowPx: 84,
    rowClass: "h-21",
    nameClass: "text-3xl",
    valueSize: "lg",
    arrowPx: 36,
    columnPx: 640,
    cardPx: 230,
    cardNameClass: "text-5xl",
    cardSize: "2xl",
    footerClass: "text-3xl",
    clockClass: "text-5xl",
  },
];

export function wallStep(width: number | undefined): WallStep {
  return WALL_STEPS.findLast((step) => (width ?? 0) >= step.minWidth) ?? WALL_STEPS[0];
}

/** How the flagged cards are laid across the top. */
export function bandColumns(width: number | undefined): { count: number; gridClass: string } {
  if ((width ?? 0) >= 1024) return { count: 3, gridClass: "grid-cols-3" };
  if ((width ?? 0) >= 672) return { count: 2, gridClass: "grid-cols-2" };
  return { count: 1, gridClass: "grid-cols-1" };
}

/**
 * How many flagged people the band holds. Unmeasured, it holds up to the maximum; measured, only
 * the cards that fit in the band's share of the screen's height, so the band never pushes the
 * footer off the screen.
 */
export function bandCapacity(
  flagged: number,
  width: number | undefined,
  height: number | undefined
): number {
  if (width === undefined || height === undefined) return Math.min(flagged, WALL_BAND_MAX);
  const rows = Math.floor((height * BAND_HEIGHT_SHARE) / wallStep(width).cardPx);
  return Math.min(flagged, WALL_BAND_MAX, rows * bandColumns(width).count);
}

/**
 * How many people the columns hold. Zero rows hold nobody; a row that does not fully fit is not
 * counted. Undefined until the columns' box is measured.
 */
export function wallColumnCapacity(
  width: number | undefined,
  height: number | undefined
): { rows: number; columns: number } | undefined {
  if (width === undefined || height === undefined) return undefined;
  const step = wallStep(width);
  return {
    rows: Math.floor(height / step.rowPx),
    columns: Math.max(1, Math.floor(width / step.columnPx)),
  };
}

/** People the columns list together, under a heading when it has one. */
export interface WallSection<T, H> {
  heading: H | null;
  people: T[];
}

/**
 * One row of a column: a heading or a person. A heading is `repeated` when it stands at the top of a
 * column its people run on into, after the column that first named it.
 */
export type WallSlot<T, H> =
  | { kind: "heading"; heading: H; repeated: boolean }
  | { kind: "person"; person: T };

/**
 * The sections laid into columns of `rows` slots that fill top to bottom, then left to right. A heading
 * takes a slot, has at least one of its people under it in the same column, and is repeated at the top
 * of a column its people run on into. When they do not fill the screen they are spread over every
 * column, as short as the columns can be, so a name stays close to its reading. The people past the
 * last slot are left out. A column of one row has no room for a heading and a person, so with fewer
 * than two rows no heading is drawn.
 */
export function intoColumns<T, H>(
  sections: WallSection<T, H>[],
  rows: number,
  columns: number
): WallSlot<T, H>[][] {
  const total = sections.reduce((count, section) => count + section.people.length, 0);
  if (rows <= 0 || total === 0) return [];
  const laid = rows < 2 ? sections.map((section) => ({ ...section, heading: null })) : sections;
  for (let height = Math.min(rows, Math.ceil(total / columns)); height < rows; height++) {
    const tried = fill(laid, height, columns);
    if (tried.placed === total) return tried.columns;
  }
  return fill(laid, rows, columns).columns;
}

function fill<T, H>(sections: WallSection<T, H>[], height: number, columns: number) {
  const laid: WallSlot<T, H>[][] = [[]];
  let placed = 0;
  const done = () => ({ columns: laid.filter((column) => column.length > 0), placed });
  for (const section of sections) {
    for (const [index, person] of section.people.entries()) {
      let column = laid[laid.length - 1];
      const heading = () => (index === 0 || column.length === 0 ? section.heading : null);
      const slots = () => (heading() === null ? 1 : 2);
      if (column.length + slots() > height) {
        if (laid.length === columns) return done();
        column = [];
        laid.push(column);
        if (slots() > height) return done();
      }
      const over = heading();
      if (over !== null) column.push({ kind: "heading", heading: over, repeated: index > 0 });
      column.push({ kind: "person", person });
      placed++;
    }
  }
  return done();
}
