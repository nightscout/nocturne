import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Trackers are used day to day, so they have a top-level page.
export const load: PageLoad = async ({ url }) => {
  redirect(308, `/trackers${url.search}`);
};
