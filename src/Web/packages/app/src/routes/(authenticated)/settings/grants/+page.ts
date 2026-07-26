import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Grants are managed within the unified Members page, alongside roles and sharing.
export const load: PageLoad = async () => {
  redirect(308, "/settings/members");
};
