import { redirect } from "@sveltejs/kit";
import type { LayoutServerLoad } from "./$types";

export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    const returnUrl = encodeURIComponent(url.pathname + url.search);
    throw redirect(303, `/auth/login?returnUrl=${returnUrl}`);
  }
};
