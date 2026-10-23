import { page } from "$app/state";
import { coachmark } from "@nocturne/coach";
import { coachFor, type CoachCopyKey } from "$lib/coach-marks/copy.svelte";
import { loopAnalysisPageAt } from "./loop-analysis-pages.svelte";

type LoopAnalysisTourKey = Extract<CoachCopyKey, `loop-analysis-${string}`>;

/** A share viewer or a guest is not toured: the tour's backdrop covers a page they only look at. */
function toured(): boolean {
  return !!page.data.user && !page.data.isGuestSession;
}

/** The coach mark for one Loop Analysis tour step, for `{@attach loopAnalysisMark(key)}`. */
export function loopAnalysisMark(key: LoopAnalysisTourKey) {
  return toured() ? coachmark(coachFor(key)) : undefined;
}

/** The mark that explains the report header's pages and Filters, on a Loop Analysis page only. */
export function loopAnalysisHeaderMark() {
  return loopAnalysisPageAt(page.url.pathname) ? loopAnalysisMark("loop-analysis-pages.switcher") : undefined;
}
