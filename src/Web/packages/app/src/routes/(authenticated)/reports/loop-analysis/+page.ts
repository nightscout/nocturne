import { redirect } from "@sveltejs/kit";
import { loopAnalysisPath } from "$lib/components/reports/loop-analysis/loop-analysis-pages.svelte";
import type { PageLoad } from "./$types";

/** The report opens on its first page, under the filters it was linked with. */
export const load: PageLoad = ({ url }) => {
  redirect(307, loopAnalysisPath("daily-profile", url.searchParams));
};
