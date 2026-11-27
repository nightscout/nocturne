import { redirect } from "@sveltejs/kit";
import { signInAddress } from "#lib/navigation/sign-in-address.js";
import type { LayoutServerLoad } from "./$types";

export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated || !locals.user) {
    throw redirect(303, signInAddress(url.pathname + url.search));
  }
};
