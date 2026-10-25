import { redirect } from "@sveltejs/kit";
import type { LayoutServerLoad } from "./$types";
import { setupStageFor } from "$lib/server/setup-hub";

/** A guided item belongs to the hub, so it has the hub's audience: an owner past the core. */
export const load: LayoutServerLoad = async ({ locals, url }) => {
  if (!locals.isAuthenticated) {
    redirect(302, `/auth/login?returnUrl=${encodeURIComponent(url.pathname)}`);
  }
  const stage = await setupStageFor(locals);
  if (stage === "core") redirect(302, "/setup");
  if (stage === "not-owner") redirect(302, "/");
  return {};
};
