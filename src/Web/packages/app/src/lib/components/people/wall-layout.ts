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

/**
 * The people split into columns that fill top to bottom, then left to right. When they do not fill
 * the screen they are spread over every column, so a name stays close to its reading.
 */
export function intoColumns<T>(people: T[], rows: number, columns: number): T[][] {
  if (rows <= 0) return [];
  const listed = people.slice(0, rows * columns);
  if (listed.length === 0) return [];
  const perColumn = Math.min(rows, Math.ceil(listed.length / columns));
  const used = Math.ceil(listed.length / perColumn);
  return Array.from({ length: used }, (_, column) =>
    listed.slice(column * perColumn, (column + 1) * perColumn)
  );
}
