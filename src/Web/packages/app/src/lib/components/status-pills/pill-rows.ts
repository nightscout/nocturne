/**
 * How many rows each status rail last filled on this device, kept in a cookie so the server reserves
 * the same rows the browser does and the rail's height holds from the first paint to its loaded
 * pills. Host-scoped, so each tenant's rails keep their own.
 */
export const PILL_ROWS_COOKIE = "nocturne-pill-rows";

export type PillRail = "desktop" | "mobile";
export type PillRows = Record<PillRail, number>;

export const MAX_PILL_ROWS = 4;
const ONE_ROW: PillRows = { desktop: 1, mobile: 1 };

const clampRows = (value: number) =>
  Number.isInteger(value) && value > 1 ? Math.min(value, MAX_PILL_ROWS) : 1;

export function parsePillRows(value: string | undefined | null): PillRows {
  const match = value ? /^(\d)\.(\d)$/.exec(value) : null;
  if (!match) return ONE_ROW;
  return { desktop: clampRows(Number(match[1])), mobile: clampRows(Number(match[2])) };
}

export function formatPillRows(rows: PillRows): string {
  return `${clampRows(rows.desktop)}.${clampRows(rows.mobile)}`;
}

/** Records the rows one rail filled, keeping the other rail's. */
export function writePillRows(rail: PillRail, rows: number): void {
  const current = parsePillRows(
    document.cookie
      .split("; ")
      .findLast((row) => row.startsWith(`${PILL_ROWS_COOKIE}=`))
      ?.slice(PILL_ROWS_COOKIE.length + 1)
  );
  const value = formatPillRows({ ...current, [rail]: rows });
  if (value === formatPillRows(current) && document.cookie.includes(`${PILL_ROWS_COOKIE}=`)) return;
  const secure = location.protocol === "https:" ? "; Secure" : "";
  document.cookie = `${PILL_ROWS_COOKIE}=${value}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
}
