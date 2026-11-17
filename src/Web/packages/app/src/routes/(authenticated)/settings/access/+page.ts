import { redirect } from "@sveltejs/kit";
import type { PageLoad } from "./$types";

// Sessions are listed on the account page, with the rest of the sign-in methods.
export const load: PageLoad = async ({ url }) => {
  redirect(308, `/settings/account${url.search}`);
};
