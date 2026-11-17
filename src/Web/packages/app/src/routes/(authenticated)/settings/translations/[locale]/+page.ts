import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

/** @see ../+page.ts */
export const load: PageLoad = async ({ params, url }) => {
  redirect(308, `/community/translations/${encodeURIComponent(params.locale)}${url.search}`);
};
