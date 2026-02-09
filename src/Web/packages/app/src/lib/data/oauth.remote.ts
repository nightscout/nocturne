/**
 * Remote functions for OAuth grants and invites management
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
    const response = await apiClient.oauth.getGrants();
    return response.grants ?? [];
  } catch (err) {
    console.error("Error loading grants:", err);
    throw error(500, "Failed to load grants");
  }
});

/**
 * Get all invites created by the authenticated user
 */
export const getInvites = query(async () => {
  const { locals } = getRequestEvent();
  const { apiClient } = locals;

  try {
    const response = await apiClient.oauth.listInvites();
    return response.invites ?? [];
  } catch (err) {
    console.error("Error loading invites:", err);
    throw error(500, "Failed to load invites");
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
      await apiClient.oauth.deleteGrant(grantId);
      await getGrants().refresh();
      return { success: true };
    } catch (err) {
      console.error("Error revoking grant:", err);
      throw error(500, "Failed to revoke grant");
    }
  }
);

/**
 * Add a follower by email
 */
export const addFollower = command(
  z.object({
    followerEmail: z.string().email("Valid email is required"),
    scopes: z.array(z.string()).min(1, "At least one scope is required"),
    label: z.string().optional(),
    temporaryPassword: z.string().optional(),
    followerDisplayName: z.string().optional(),
  }),
  async ({ followerEmail, scopes, label, temporaryPassword, followerDisplayName }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.oauth.createFollowerGrant({
        followerEmail,
        scopes,
        label: label || undefined,
        temporaryPassword: temporaryPassword || undefined,
        followerDisplayName: followerDisplayName || undefined,
      });
      await getGrants().refresh();
      return { success: true };
    } catch (err) {
      console.error("Error adding follower:", err);
      throw error(500, "Failed to add follower");
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
      await apiClient.oauth.updateGrant(grantId, {
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

/**
 * Create a follower invite link
 */
export const createInvite = command(
  z.object({
    scopes: z.array(z.string()).min(1, "At least one scope is required"),
    label: z.string().optional(),
    expiresInDays: z.number().default(7),
    maxUses: z.number().optional(),
  }),
  async ({ scopes, label, expiresInDays, maxUses }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const response = await apiClient.oauth.createInvite({
        scopes,
        label: label || undefined,
        expiresInDays,
        maxUses,
      });
      await getInvites().refresh();
      return {
        success: true,
        inviteUrl: response.inviteUrl,
        token: response.token,
      };
    } catch (err) {
      console.error("Error creating invite:", err);
      throw error(500, "Failed to create invite");
    }
  }
);

/**
 * Revoke an invite
 */
export const revokeInvite = command(
  z.object({
    inviteId: z.string().min(1, "Invite ID is required"),
  }),
  async ({ inviteId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      await apiClient.oauth.revokeInvite(inviteId);
      await getInvites().refresh();
      return { success: true };
    } catch (err) {
      console.error("Error revoking invite:", err);
      throw error(500, "Failed to revoke invite");
    }
  }
);
