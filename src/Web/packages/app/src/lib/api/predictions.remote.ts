/** Remote functions for glucose predictions using oref algorithms */
import { getRequestEvent, query } from "$app/server";
import { z } from "zod";
import { error } from "@sveltejs/kit";
import { PREDICTIONS_UNAVAILABLE } from "#lib/api/predictions-messages.js";
import { errorStatus } from "#lib/forms/submit-error.js";
import { toPredictionData } from "./prediction-data";

const getPredictionsSchema = z.object({
  profileId: z.string().optional(),
});

export type { PredictionPoint, PredictionCurves, PredictionData } from "@nocturne/charts/core";

/**
 * Get glucose predictions based on current data. Returns predicted glucose
 * values with timestamps for charting.
 */
export const getPredictions = query(getPredictionsSchema, async (props) => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const response = await apiClient.predictions.getPredictions(
      props.profileId
    );

    return toPredictionData(response);
  } catch (err) {
    // 404 means predictions are not configured — this is expected for optional features
    if (errorStatus(err) === 404) {
      return null;
    }
    console.error("Error loading predictions:", err);
    throw error(500, PREDICTIONS_UNAVAILABLE);
  }
});

/** Get the status of the prediction service */
export const getPredictionStatus = query(z.object({}), async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const response = await apiClient.predictions.getStatus();
    return {
      available: response.available ?? false,
      source: response.source ?? null,
    };
  } catch (err) {
    console.error("Error checking prediction status:", err);
    return {
      available: false,
      source: null,
    };
  }
});
