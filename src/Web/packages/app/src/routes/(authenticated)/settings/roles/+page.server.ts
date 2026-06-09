import { redirect } from "@sveltejs/kit";

// Roles are now managed within the unified Sharing & Privacy page.
export const load = async () => {
  throw redirect(301, "/settings/members");
};
