/** Remote functions for user preferences management */
import { getRequestEvent, command } from "$app/server";
import { z } from "zod";

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
