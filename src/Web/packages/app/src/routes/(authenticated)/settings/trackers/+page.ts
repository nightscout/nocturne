import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Trackers left settings for their own place in the sidebar.
export const load: PageLoad = async ({ url }) => {
  redirect(308, `/trackers${url.search}`);
};
