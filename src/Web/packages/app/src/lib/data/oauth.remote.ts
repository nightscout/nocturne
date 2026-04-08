/**
 * Remote functions for OAuth grants management
 * Shared across connectors and grants pages
 */
import { getRequestEvent, query, command } from "$app/server";
import { z } from "zod";
import { error } from "@sveltejs/kit";

// ============================================================================
// Query Functions
// ============================================================================

/**
 * Get all grants for the authenticated user
 */
export const getGrants = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const response = await apiClient.oAuth.getGrants();
    return response.grants ?? [];
  } catch (err) {
    console.error("Error loading grants:", err);
    throw error(500, "Failed to load grants");
  }
});

// ============================================================================
// Command Functions
// ============================================================================

/**
 * Revoke (delete) a grant
 */
export const revokeGrant = command(
  z.object({
    grantId: z.string().min(1, "Grant ID is required"),
  }),
  async ({ grantId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.oAuth.deleteGrant(grantId);
      await getGrants().refresh();
      return { success: true };
    } catch (err) {
      console.error("Error revoking grant:", err);
      throw error(500, "Failed to revoke grant");
    }
  }
);

/**
 * Update a grant's label and/or scopes
 */
export const updateGrant = command(
  z.object({
    grantId: z.string().min(1, "Grant ID is required"),
    label: z.string().optional(),
    scopes: z.array(z.string()).optional(),
  }),
  async ({ grantId, label, scopes }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.oAuth.updateGrant(grantId, {
        label: label || undefined,
        scopes,
      });
      await getGrants().refresh();
      return { success: true };
    } catch (err) {
      console.error("Error updating grant:", err);
      throw error(500, "Failed to update grant");
    }
  }
);
