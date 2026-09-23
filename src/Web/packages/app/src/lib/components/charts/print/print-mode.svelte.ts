import { flushSync } from "svelte";

/** Set on the document root by `printReport()` while the page is laid out at paper width. */
export const PRINT_LAYOUT_CLASS = "report-print-layout";

/**
 * Whether the page is being printed. Use it where paper needs different markup,
 * not just different CSS: every row instead of a page, a chart sized to fit.
 * It turns on with `printReport()`'s paper-width layout, so charts re-measure
 * before the dialog opens, and with the browser's own Print command.
 * Create it during component init.
 */
export class PrintMode {
  active = $state(false);

  constructor() {
    $effect(() => {
      const root = document.documentElement;
      const mql = window.matchMedia("print");
      const sync = () => (this.active = mql.matches || root.classList.contains(PRINT_LAYOUT_CLASS));
      const on = () => flushSync(() => (this.active = true));
      const classes = new MutationObserver(sync);

      sync();
      classes.observe(root, { attributes: true, attributeFilter: ["class"] });
      mql.addEventListener("change", sync);
      window.addEventListener("beforeprint", on);
      window.addEventListener("afterprint", sync);
      return () => {
        classes.disconnect();
        mql.removeEventListener("change", sync);
        window.removeEventListener("beforeprint", on);
        window.removeEventListener("afterprint", sync);
      };
    });
  }
}
