import { redirect } from "@sveltejs/kit";
import type { PageServerLoad } from "./$types";

/**
 * The access-request endpoints require the platform_admin role, so without this
 * a non-admin typing the URL got a 403 error page from the first query rather
 * than being sent somewhere useful. Mirrors `settings/admin/+layout.server.ts`.
 */
export const load: PageServerLoad = async ({ locals }) => {
  if (!locals.isPlatformAdmin) {
    throw redirect(303, "/settings");
  }
  return { isPlatformAdmin: true };
};
