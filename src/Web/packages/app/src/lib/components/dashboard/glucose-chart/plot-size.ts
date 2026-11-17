/**
 * The size of the dashboard's main plot box on this device, written by the live chart so the server
 * can render the plot at that size before the page's scripts run (`#lib/server/plot-snapshot.js`).
 */
export const PLOT_SIZE_COOKIE = "nocturne-plot-size";

export interface PlotSize {
  width: number;
  height: number;
  /** The card is narrower than its desktop layout, so the plot uses the narrow gutters. */
  compact: boolean;
}

const PATTERN = /^(\d{2,5})x(\d{2,4})x([01])$/;

export function formatPlotSize(size: PlotSize): string {
  return `${Math.round(size.width)}x${Math.round(size.height)}x${size.compact ? 1 : 0}`;
}

export function parsePlotSize(value: string | undefined | null): PlotSize | null {
  const match = value ? PATTERN.exec(value) : null;
  if (!match) return null;
  const width = Number(match[1]);
  const height = Number(match[2]);
  if (width < 100 || width > 10000 || height < 100 || height > 2000) return null;
  return { width, height, compact: match[3] === "1" };
}

/** Records the plot's size for the next document; a no-op when it is unchanged. */
export function writePlotSize(size: PlotSize): void {
  const value = formatPlotSize(size);
  if (document.cookie.split("; ").includes(`${PLOT_SIZE_COOKIE}=${value}`)) return;
  const secure = location.protocol === "https:" ? "; Secure" : "";
  document.cookie = `${PLOT_SIZE_COOKIE}=${value}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
}
