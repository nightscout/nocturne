import { getContext, setContext } from "svelte";
import { PRINT_LAYOUT_CLASS } from "#lib/components/charts/print/print-mode.svelte.js";

/**
 * Print-document metadata the reports layout cannot derive itself. A period is
 * needed by a report with its own period controls (comparison, year overview,
 * day in review).
 */
export interface ReportPrintMeta {
  /**
   * Inclusive YYYY-MM-DD bounds, or free text for periods that are not one
   * range.
   */
  period?: { from: string; to: string } | { label: string };
}

export class ReportPrintContext {
  printing = $state(false);
  #read = $state<(() => ReportPrintMeta) | null>(null);
  #prepare: (() => Promise<void>) | null = null;
  #preparationVersion = 0;

  get preparationVersion(): number {
    return this.#preparationVersion;
  }

  get meta(): ReportPrintMeta {
    return this.#read?.() ?? {};
  }

  provide(read: () => ReportPrintMeta): () => void {
    this.#read = read;
    return () => {
      if (this.#read === read) this.#read = null;
    };
  }

  providePreparation(prepare: () => Promise<void>): () => void {
    this.#preparationVersion++;
    this.#prepare = prepare;
    return () => {
      if (this.#prepare === prepare) {
        this.#prepare = null;
        this.#preparationVersion++;
      }
    };
  }

  async prepare(): Promise<void> {
    await this.#prepare?.();
  }
}

const KEY = "nocturne:app:report-print";

export function createReportPrintContext(): ReportPrintContext {
  return setContext(KEY, new ReportPrintContext());
}

export function getReportPrintContext(): ReportPrintContext | undefined {
  return getContext<ReportPrintContext | undefined>(KEY);
}

/** Declare this report's print period. Call during component init. */
export function setReportPrintMeta(read: () => ReportPrintMeta): void {
  const ctx = getContext<ReportPrintContext | undefined>(KEY);
  if (!ctx) return;
  $effect.pre(() => ctx.provide(read));
}

export function setReportPrintPreparation(prepare: () => Promise<void>): void {
  const ctx = getContext<ReportPrintContext | undefined>(KEY);
  if (!ctx) return;
  $effect.pre(() => ctx.providePreparation(prepare));
}

/**
 * Printable width inside the `@page` margins in `app.css`: A4's 210mm less two
 * 1.2cm margins. US Letter is 6mm wider, so a report laid out at this width
 * fits either.
 */
const PAPER_CONTENT_MM = 186;

function settle(): Promise<void> {
  return new Promise((resolve) =>
    requestAnimationFrame(() =>
      requestAnimationFrame(() => setTimeout(resolve, 250))
    )
  );
}

/**
 * Print the report laid out at paper width. Charts size themselves from a
 * ResizeObserver, which never runs for the print layout itself. Printed
 * straight from a wide screen, every chart would clip at its screen width. So
 * the page narrows first and waits for the charts to re-measure.
 */
export async function printReport(
  prepare?: () => Promise<void>,
  isCurrent?: () => boolean
): Promise<void> {
  await prepare?.();
  const root = document.documentElement;
  root.style.setProperty("--report-print-width", `${PAPER_CONTENT_MM}mm`);
  root.classList.add(PRINT_LAYOUT_CLASS);
  try {
    await settle();
    if (isCurrent && !isCurrent())
      throw new Error("Report changed before printing");
    window.print();
  } finally {
    // print() blocks until the dialog closes, and afterprint never fires where
    // the dialog is suppressed, so the paper layout is undone here instead.
    root.classList.remove(PRINT_LAYOUT_CLASS);
    root.style.removeProperty("--report-print-width");
  }
}

/**
 * The browser's own Print command reaches `beforeprint` with no time to
 * re-measure, so any chart wider than the paper is scaled down to it instead. A
 * scaled chart keeps every point, where an unscaled one loses its right edge.
 * Returns the listener teardown.
 */
export function installPrintFitFallback(
  container: () => HTMLElement | null
): () => void {
  const restore: (() => void)[] = [];

  const fit = () => {
    const root = container();
    if (
      !root ||
      document.documentElement.classList.contains(PRINT_LAYOUT_CLASS)
    )
      return;
    const paper = millimetresToPx(PAPER_CONTENT_MM);
    for (const svg of root.querySelectorAll<SVGSVGElement>("svg")) {
      const { width, height } = svg.getBoundingClientRect();
      // Small multiples print two to a row, so each has half the paper. A report chart's plot sits in
      // containers of the width it measured, which a narrower svg alone would not shrink, so the
      // whole chart is scaled instead: a transform, which the chart does not measure again.
      const multiple = svg.closest<HTMLElement>(
        '[data-layout="multiples"] [data-testid="report-layered-chart"]'
      );
      const limit = multiple
        ? (paper - MULTIPLES_GAP_PX) / 2 - MULTIPLES_GAP_PX
        : paper;
      if (width <= limit || svg.parentElement?.closest("svg")) continue;
      if (multiple) {
        const scale = limit / width;
        const box = multiple.getBoundingClientRect().height;
        const prev = {
          transform: multiple.style.transform,
          origin: multiple.style.transformOrigin,
          margin: multiple.style.marginBottom,
        };
        multiple.style.transform = `scale(${scale})`;
        multiple.style.transformOrigin = "top left";
        multiple.style.marginBottom = `${box * (scale - 1)}px`;
        restore.push(() => {
          multiple.style.transform = prev.transform;
          multiple.style.transformOrigin = prev.origin;
          multiple.style.marginBottom = prev.margin;
        });
        continue;
      }
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

/**
 * The gap between two small multiples in a row (the charts' `gap-x-6`), also kept clear at each
 * one's right edge: the paper a browser prints on can be a little narrower than
 * {@link PAPER_CONTENT_MM}.
 */
const MULTIPLES_GAP_PX = 24;

function millimetresToPx(mm: number): number {
  return (mm * 96) / 25.4;
}
