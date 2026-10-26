import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// The transition status is the Status tab of the migration page. Redirecting in `load`
// works without JS and doesn't flash a placeholder page.
export const load: PageLoad = async ({ url }) => {
  redirect(308, `/settings/migration${url.search}`);
};
