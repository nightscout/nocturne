import { redirect } from "@sveltejs/kit";
import { satisfiesScope } from "#lib/authorization/scopes.js";
import type { PageLoad } from "./$types";

export const load: PageLoad = async ({ parent }) => {
  const { effectivePermissions } = await parent();
  if (!satisfiesScope(effectivePermissions ?? [], "sharing.manage")) {
    redirect(303, "/clock");
  }
};
