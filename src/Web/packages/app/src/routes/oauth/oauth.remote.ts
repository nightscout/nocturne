/**
 * Remote functions for OAuth device authorization and consent flows
 */
import { getRequestEvent, query, command } from "$app/server";
import { z } from "zod";
import { error, redirect } from "@sveltejs/kit";

// ============================================================================
// Query Functions
// ============================================================================

/**
 * Get device code info for the device authorization page
 */
export const getDeviceInfo = query(
  z.object({
    userCode: z.string().min(1, "User code is required"),
  }),
  async ({ userCode }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      return await apiClient.oauth.getDeviceInfo(userCode);
    } catch (err) {
      console.error("Error looking up device code:", err);
      throw error(400, "Invalid or expired device code");
    }
  }
);

/**
 * Get client info for the consent page
 */
export const getClientInfo = query(
  z.object({
    clientId: z.string().min(1, "Client ID is required"),
  }),
  async ({ clientId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      return await apiClient.oauth.getClientInfo(clientId);
    } catch (err) {
      console.error("Error fetching client info:", err);
      // Return a default unknown client info if fetch fails
      return {
        clientId,
        displayName: null,
        isKnown: false,
        homepage: null,
      };
    }
  }
);

// ============================================================================
// Command Functions
// ============================================================================

/**
 * Approve a device authorization request
 */
export const approveDevice = command(
  z.object({
    userCode: z.string().min(1, "User code is required"),
  }),
  async ({ userCode }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    try {
      await apiClient.oauth.deviceApprove(userCode, true);
      return { success: true };
    } catch (err) {
      console.error("Error approving device:", err);
      throw error(400, "The device code has expired or is no longer valid");
    }
  }
);

/**
 * Deny a device authorization request
 */
export const denyDevice = command(
  z.object({
    userCode: z.string().min(1, "User code is required"),
  }),
  async ({ userCode }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    if (!locals.isAuthenticated) {
      throw redirect(303, "/auth/login");
    }

    try {
      await apiClient.oauth.deviceApprove(userCode, false);
      return { denied: true };
    } catch (err) {
      console.error("Error denying device:", err);
      throw error(400, "The device code has expired or is no longer valid");
    }
  }
);
