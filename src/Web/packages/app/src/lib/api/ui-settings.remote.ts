/**
 * Remote functions for the tenant's UI settings blob.
 *
 * The API takes and returns the whole UISettingsConfiguration document, so a
 * section update is a read-modify-write: fetch the stored document, replace one
 * section, save it back. Callers get the persisted value from getUiSettings()
 * rather than an in-memory copy, so nothing is lost on reload.
 */
import { getRequestEvent, query, command } from "$app/server";
import { error } from "@sveltejs/kit";
import type {
  DataQualitySettings,
  FeatureSettings,
  UISettingsConfiguration,
} from "$lib/api/generated/nocturne-api-client";
import {
  DataQualitySettingsSchema,
  FeatureSettingsSchema,
} from "$lib/api/generated/schemas";

export const getUiSettings = query(async () => {
  const { locals } = getRequestEvent();

  try {
    return await locals.apiClient.uiSettings.getUISettings();
  } catch (err) {
    console.error("Error loading UI settings:", err);
    throw error(500, "Failed to load settings");
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
