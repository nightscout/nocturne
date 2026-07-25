import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Therapy settings are managed as profiles. Redirecting in `load` (rather than
// goto() in onMount) works without JS and doesn't flash a placeholder page.
export const load: PageLoad = async () => {
  redirect(308, "/settings/profile");
};
