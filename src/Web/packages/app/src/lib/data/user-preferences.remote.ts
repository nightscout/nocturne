/**
 * Remote functions for user preferences management
 */
import { getRequestEvent, command } from "$app/server";
import { z } from "zod";

/**
 * Update user preferences request
 */
interface UpdateUserPreferencesRequest {
  preferredLanguage?: string;
}

/**
 * User preferences response
 */
interface UserPreferencesResponse {
  preferredLanguage?: string;
}

const updateLanguageSchema = z.object({
  preferredLanguage: z.string(),
});

/**
 * Update the current user's language preference
 * @param preferredLanguage The language code to set (e.g., "en", "fr")
 */
export const updateLanguagePreference = command(
  updateLanguageSchema,
  async ({ preferredLanguage }): Promise<UserPreferencesResponse | null> => {
    const { locals } = getRequestEvent();

    // Only update if user is authenticated
    if (!locals.isAuthenticated || !locals.user) {
      console.log("User not authenticated, skipping backend language preference update");
      return null;
    }

    try {
      // Call the API directly since NSwag client may not have the endpoint yet
      const response = await fetch("/api/v4/user/preferences", {
        method: "PATCH",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify({ preferredLanguage } satisfies UpdateUserPreferencesRequest),
      });

      if (!response.ok) {
        const errorData = await response.json().catch(() => ({}));
        console.error("Failed to update language preference:", errorData);
        return null;
      }

      return (await response.json()) as UserPreferencesResponse;
    } catch (err) {
      console.error("Error updating language preference:", err);
      // Don't throw - failing to save preference shouldn't break the UI
      return null;
    }
  }
);
