/**
 * Platform Settings Admin Remote Functions
 *
 * Server-side wrappers around the generated PlatformSettingsClient for use
 * from the platform admin settings tab.
 */

import { z } from "zod";
import { query, command, getRequestEvent } from "$app/server";

function getApiClient() {
  const event = getRequestEvent();
  if (!event?.locals?.apiClient) {
    throw new Error("API client not configured");
  }
  return event.locals.apiClient;
}

export const getPlatformSettings = query(async () => {
  return getApiClient().platformSettings.getAll();
});

const upsertSchema = z.object({
  category: z.string().min(1),
  enabled: z.boolean(),
  fields: z.record(z.string(), z.string()),
});

export const upsertPlatformSettings = command(upsertSchema, async ({ category, enabled, fields }) => {
  return getApiClient().platformSettings.upsert(category, { enabled, fields });
});
