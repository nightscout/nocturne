import { getContext, setContext } from "svelte";
import { PRINT_LAYOUT_CLASS } from "$lib/components/charts/print/print-mode.svelte";

/**
 * Print-document metadata the reports layout cannot derive itself. A title is
 * needed for a route the report registry does not list. A period is needed by
 * a report with its own period controls (comparison, year overview, day in review).
 */
export interface ReportPrintMeta {
  title?: string;
  /** Inclusive YYYY-MM-DD bounds, or free text for periods that are not one range. */
  period?: { from: string; to: string } | { label: string };
}

export class ReportPrintContext {
  #read = $state<(() => ReportPrintMeta) | null>(null);

  get meta(): ReportPrintMeta {
    return this.#read?.() ?? {};
  }

  provide(read: () => ReportPrintMeta): () => void {
    this.#read = read;
    return () => {
      if (this.#read === read) this.#read = null;
    };
  }
}

const KEY = Symbol("report-print");

export function createReportPrintContext(): ReportPrintContext {
  return setContext(KEY, new ReportPrintContext());
}

/** Declare this report's print title and/or period. Call during component init. */
export function setReportPrintMeta(read: () => ReportPrintMeta): void {
  const ctx = getContext<ReportPrintContext | undefined>(KEY);
  if (!ctx) return;
  $effect.pre(() => ctx.provide(read));
}

/**
 * Printable width inside the `@page` margins in `app.css`: A4's 210mm less two
 * 1.2cm margins. US Letter is 6mm wider, so a report laid out at this width
 * fits either.
 */
const PAPER_CONTENT_MM = 186;

function settle(): Promise<void> {
  return new Promise((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => setTimeout(resolve, 250)))
  );
}

/**
 * Print the report laid out at paper width. Charts size themselves from a
 * ResizeObserver, which never runs for the print layout itself. Printing
 * straight from a wide screen would clip every chart at its screen width, so
 * the page narrows first and waits for the charts to re-measure.
 */
export async function printReport(): Promise<void> {
  const root = document.documentElement;
  root.style.setProperty("--report-print-width", `${PAPER_CONTENT_MM}mm`);
  root.classList.add(PRINT_LAYOUT_CLASS);
  window.addEventListener("afterprint", () => root.classList.remove(PRINT_LAYOUT_CLASS), { once: true });
  await settle();
  window.print();
}

/**
 * The browser's own Print command reaches `beforeprint` with no time to
 * re-measure, so any chart wider than the paper is scaled down to it instead.
 * A scaled chart keeps every point, where an unscaled one loses its right edge.
 * Returns the listener teardown.
 */
export function installPrintFitFallback(container: () => HTMLElement | null): () => void {
  const restore: (() => void)[] = [];

  const fit = () => {
    const root = container();
    if (!root || document.documentElement.classList.contains(PRINT_LAYOUT_CLASS)) return;
    const limit = millimetresToPx(PAPER_CONTENT_MM);
    for (const svg of root.querySelectorAll<SVGSVGElement>("svg")) {
      const { width, height } = svg.getBoundingClientRect();
      if (width <= limit || svg.parentElement?.closest("svg")) continue;
      const prev = {
        viewBox: svg.getAttribute("viewBox"),
        width: svg.style.width,
        height: svg.style.height,
      };
      if (!prev.viewBox) svg.setAttribute("viewBox", `0 0 ${width} ${height}`);
      svg.style.width = "100%";
      svg.style.height = "auto";
      restore.push(() => {
        if (prev.viewBox === null) svg.removeAttribute("viewBox");
        svg.style.width = prev.width;
        svg.style.height = prev.height;
      });
    }
  };
  const unfit = () => {
    for (const undo of restore.splice(0)) undo();
  };

  window.addEventListener("beforeprint", fit);
  window.addEventListener("afterprint", unfit);
  return () => {
    window.removeEventListener("beforeprint", fit);
    window.removeEventListener("afterprint", unfit);
    unfit();
  };
}

function millimetresToPx(mm: number): number {
  return (mm * 96) / 25.4;
}
