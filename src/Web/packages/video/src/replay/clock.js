import { capturedAt } from "virtual:captured-at";

/**
 * The browser clock reads as the moment the report's fixture was captured, so a page's default date
 * range and every "last updated" line agree with the data, on every frame and every worker.
 */
const RealDate = Date;
globalThis.Date = class extends RealDate {
  constructor(...args) {
    super(...(args.length ? args : [capturedAt]));
  }
  static now() {
    return capturedAt;
  }
};
