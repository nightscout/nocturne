/** Remote functions for user preferences management */
import { getRequestEvent, command } from "$app/server";
import { z } from "zod";
import type { UserDisplayPreferences } from "$lib/api";
import { UserDisplayPreferencesSchema } from "$lib/api/generated/schemas";

const updateLanguageSchema = z.object({
  preferredLanguage: z.string(),
});

/**
 * Update the current user's language preference
 *
 * @param preferredLanguage The language code to set (e.g., "en", "fr")
 */
export const updateLanguagePreference = command(
  updateLanguageSchema,
  async ({ preferredLanguage }) => {
    const { locals } = getRequestEvent();

    // Only update if user is authenticated
    if (!locals.isAuthenticated || !locals.user) {
      console.log(
        "User not authenticated, skipping backend language preference update"
      );
      return null;
    }

    try {
      return await locals.apiClient.userPreferences.updatePreferences({
        preferredLanguage,
      });
    } catch (err) {
      console.error("Error updating language preference:", err);
      // Don't throw - failing to save preference shouldn't break the UI
      return null;
    }
  }
);

/**
 * Update the current user's display preferences (units, time format, theme, chart
 * style, widgets). Merged server-side over the stored blob, so a partial payload is fine.
 */
export const updateDisplayPreferences = command(
  UserDisplayPreferencesSchema,
  async (preferences) => {
    const { locals } = getRequestEvent();

    // Only persist for authenticated users; guests/anonymous keep client-only prefs.
    if (!locals.isAuthenticated || !locals.user) {
      return null;
    }

    try {
      return await locals.apiClient.userPreferences.updatePreferences({
        // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- z.fromJSONSchema infers unknown; UserDisplayPreferencesSchema validates the shape at runtime
        preferences: preferences as UserDisplayPreferences,
      });
    } catch (err) {
      console.error("Error updating display preferences:", err);
      // Don't throw - failing to save preference shouldn't break the UI
      return null;
    }
  }
);
