/** Read of the tenant's UI settings blob. Sections save through generated commands. */
import { getRequestEvent, query } from "$app/server";
import { error, redirect } from "@sveltejs/kit";
import { errorStatus } from "#lib/forms/submit-error.js";
import { SETTINGS_LOAD_FAILED } from "./ui-settings-messages";

export const getUiSettings = query(async () => {
  const { locals } = getRequestEvent();

  try {
    return await locals.apiClient.uiSettings.getUISettings();
  } catch (err) {
    // Same 401 handling as a generated query: the settings pages are behind the
    // authenticated layout, so an expired session has to reach the login route.
    if (errorStatus(err) === 401) {
      if (locals.isShareHost) throw error(401, "Unauthorized");
      throw redirect(302, "/auth/login");
    }

    console.error("Error loading UI settings:", err);
    throw error(500, SETTINGS_LOAD_FAILED);
  }
});
