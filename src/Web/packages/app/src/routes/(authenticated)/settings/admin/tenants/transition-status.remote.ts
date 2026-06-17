/**
 * Remote function to fetch the multitenancy transition status.
 * Used by the tenant admin settings to show a notice when BaseDomain
 * is configured (multitenancy enabled).
 */
import { getRequestEvent, query } from "$app/server";

export interface TransitionStatus {
  multitenancyEnabled: boolean;
  baseDomain?: string | null;
  message?: string | null;
}

/**
 * Get the multitenancy transition status from the platform API.
 */
export const getTransitionStatus = query(async () => {
  const { locals, fetch } = getRequestEvent();

  try {
    const response = await fetch(
      `${locals.apiClient.baseUrl}/api/v4/platform/transition-status`,
    );

    if (!response.ok) {
      console.error(
        "Failed to fetch transition status:",
        response.status,
        response.statusText,
      );
      return { multitenancyEnabled: false } as TransitionStatus;
    }

    return (await response.json()) as TransitionStatus;
  } catch (err) {
    console.error("Error fetching transition status:", err);
    return { multitenancyEnabled: false } as TransitionStatus;
  }
});
