import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

/** @see ../+page.ts */
export const load: PageLoad = async ({ params }) => {
  redirect(308, `/community/translations/${encodeURIComponent(params.locale)}`);
};
