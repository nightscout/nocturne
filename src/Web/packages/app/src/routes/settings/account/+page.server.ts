import type { PageServerLoad } from "./$types";

/**
 * Account settings page server load
 * Returns user data from the layout
 */
export const load: PageServerLoad = async ({ parent }) => {
  const parentData = await parent();
  return {
    user: parentData.user,
    isAuthenticated: parentData.isAuthenticated,
  };
};
