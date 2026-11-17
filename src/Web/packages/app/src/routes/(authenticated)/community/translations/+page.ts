import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// The locale picker is on the community page.
export const load: PageLoad = async () => {
  redirect(308, "/community");
};
