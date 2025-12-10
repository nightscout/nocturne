/**
 * Remote functions for event type data
 */
import { getRequestEvent, query } from "$app/server";
import { error } from "@sveltejs/kit";
import type {
  TreatmentEventTypesMetadata,
  EventTypeConfiguration,
} from "$lib/api/generated/nocturne-api-client";

/**
 * Fetch event type configurations from the backend.
 * This is the single source of truth for event type metadata.
 */
export const fetchEventTypeConfigurations = query(async (): Promise<EventTypeConfiguration[]> => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const metadata = await apiClient.metadata.getTreatmentEventTypes();
    return metadata.configurations ?? [];
  } catch (err) {
    console.error("Error fetching event type configurations:", err);
    throw error(500, "Failed to fetch event type configurations");
  }
});

/**
 * Fetch full event types metadata from the backend.
 */
export const fetchEventTypesMetadata = query(async (): Promise<TreatmentEventTypesMetadata> => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    return await apiClient.metadata.getTreatmentEventTypes();
  } catch (err) {
    console.error("Error fetching event types metadata:", err);
    throw error(500, "Failed to fetch event types metadata");
  }
});
