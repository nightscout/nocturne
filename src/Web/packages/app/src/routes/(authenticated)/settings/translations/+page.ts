import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Translating Nocturne is community work, not a setting.
export const load: PageLoad = async () => {
  redirect(308, "/community");
};
