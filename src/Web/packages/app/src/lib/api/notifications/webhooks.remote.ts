import { z } from "zod";
import { error } from "@sveltejs/kit";
import { getRequestEvent, query, command } from "$app/server";
import type { WebhookNotificationSettings, WebhookTestResult } from "$lib/api";

const WebhookSettingsSchema = z.object({
  enabled: z.boolean(),
  urls: z.array(z.string()).optional(),
  secret: z.string().optional().nullable(),
});

const WebhookTestSchema = z.object({
  urls: z.array(z.string()).optional(),
  secret: z.string().optional().nullable(),
});

export const getWebhookSettings = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.webhookSettings.getWebhookSettings();
  } catch (err) {
    console.error("Failed to load webhook settings:", err);
    throw error(500, "Failed to load webhook settings");
  }
});

export const saveWebhookSettings = command(
  WebhookSettingsSchema,
  async (settings) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      return await apiClient.webhookSettings.saveWebhookSettings(
        settings as WebhookNotificationSettings
      );
    } catch (err) {
      console.error("Failed to save webhook settings:", err);
      throw error(500, "Failed to save webhook settings");
    }
  }
);

export const testWebhookSettings = command(
  WebhookTestSchema,
  async (request) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const client = apiClient.webhookSettings as {
      testWebhookSettings?: (payload: unknown) => Promise<WebhookTestResult>;
    };

    if (!client.testWebhookSettings) {
      throw error(
        501,
        "Webhook test endpoint unavailable. Regenerate the API client."
      );
    }

    try {
      return await client.testWebhookSettings(request as any) as WebhookTestResult;
    } catch (err) {
      console.error("Failed to test webhook settings:", err);
      throw error(500, "Failed to test webhook settings");
    }
  }
);
