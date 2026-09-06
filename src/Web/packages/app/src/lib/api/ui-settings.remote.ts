/**
 * Remote functions for the tenant's UI settings blob.
 *
 * The API takes and returns the whole UISettingsConfiguration document, so a
 * section update is a read-modify-write: fetch the stored document, replace one
 * section, save it back. Callers get the persisted value from getUiSettings()
 * rather than an in-memory copy, so nothing is lost on reload.
 */
import { getRequestEvent, query, command } from "$app/server";
import { error, redirect } from "@sveltejs/kit";
import type {
  DataQualitySettings,
  FeatureSettings,
  UISettingsConfiguration,
} from "$lib/api/generated/nocturne-api-client";
import {
  DataQualitySettingsSchema,
  FeatureSettingsSchema,
} from "$lib/api/generated/schemas";
import { errorStatus } from "$lib/forms/submit-error";
import { SETTINGS_LOAD_FAILED } from "./ui-settings-messages";

export const getUiSettings = query(async () => {
  const { locals } = getRequestEvent();

  try {
    return await locals.apiClient.uiSettings.getUISettings();
  } catch (err) {
    // Same 401 handling as a generated query: the settings pages are behind the
    // authenticated layout, so an expired session has to reach the login route.
    if (errorStatus(err) === 401) {
      const { request, url } = getRequestEvent();
      const host =
        request.headers.get("x-forwarded-host") ??
        request.headers.get("host") ??
        "";
      if (/^[^.]+\.share\./i.test(host)) throw error(401, "Unauthorized");

      throw redirect(
        302,
        `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`
      );
    }

    console.error("Error loading UI settings:", err);
    throw error(500, SETTINGS_LOAD_FAILED);
  }
});

async function saveUiSettingsSection(patch: Partial<UISettingsConfiguration>) {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const current = await apiClient.uiSettings.getUISettings();
    const saved = await apiClient.uiSettings.saveUISettings({
      ...current,
      ...patch,
    });
    await getUiSettings().refresh();
    return saved;
  } catch (err) {
    console.error("Error saving UI settings:", err);
    throw error(500, "Failed to save settings");
  }
}

/** Persists sleep schedule and compression-low detection. */
export const saveDataQualitySettings = command(
  DataQualitySettingsSchema,
  async (dataQuality) =>
    saveUiSettingsSection({
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- z.fromJSONSchema infers unknown; DataQualitySettingsSchema validates the shape at runtime
      dataQuality: dataQuality as DataQualitySettings,
    })
);

/** Persists display preferences, dashboard widgets and tracker pills. */
export const saveFeatureSettings = command(
  FeatureSettingsSchema,
  async (features) =>
    saveUiSettingsSection({
      // eslint-disable-next-line @typescript-eslint/consistent-type-assertions -- z.fromJSONSchema infers unknown; FeatureSettingsSchema validates the shape at runtime
      features: features as FeatureSettings,
    })
);
