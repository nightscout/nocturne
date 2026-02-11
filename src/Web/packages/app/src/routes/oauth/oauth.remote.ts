/**
 * Remote functions for OAuth device authorization and consent flows
 */
import { getRequestEvent, query, command, form } from "$app/server";
import { z } from "zod";
import { error, invalid, redirect } from "@sveltejs/kit";

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

// ============================================================================
// Form Functions (for use in form actions)
// ============================================================================

// ============================================================================
// Device Flow Form Functions
// ============================================================================

const deviceLookupSchema = z.object({
  user_code: z.string().min(1, "User code is required"),
});

/**
 * Form handler to look up a device code
 */
export const lookupDeviceForm = form(deviceLookupSchema, async (data, issue) => {
  const event = getRequestEvent();
  if (!event) {
    throw new Error("Request event not available");
  }
  const { apiClient } = event.locals;

  try {
    const deviceInfo = await apiClient.oauth.getDeviceInfo(data.user_code);
    return {
      deviceInfo: {
        userCode: deviceInfo.userCode ?? data.user_code,
        clientId: deviceInfo.clientId ?? "",
        displayName: deviceInfo.clientDisplayName ?? null,
        isKnown: deviceInfo.isKnownClient ?? false,
        scopes: deviceInfo.scopes ?? [],
        isExpired: deviceInfo.isExpired ?? false,
      },
    };
  } catch (err) {
    console.error("Error looking up device code:", err);
    invalid(issue.user_code("Invalid or expired device code. Please check the code and try again."));
  }
});

const deviceApproveSchema = z.object({
  user_code: z.string().min(1, "User code is required"),
});

/**
 * Helper to call device-approve endpoint with proper content type
 */
async function callDeviceApprove(
  event: ReturnType<typeof getRequestEvent>,
  userCode: string,
  approved: boolean
): Promise<void> {
  const { apiClient } = event!.locals;

  const body = new URLSearchParams();
  body.set("user_code", userCode);
  body.set("approved", approved.toString());

  const response = await event!.fetch(`${apiClient.baseUrl}/api/oauth/device-approve`, {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
    },
    body: body.toString(),
  });

  if (!response.ok) {
    const errorText = await response.text();
    console.error("Device approve failed:", response.status, errorText);
    throw new Error(`Device approval failed: ${response.status}`);
  }
}

/**
 * Form handler to approve a device authorization request
 */
export const approveDeviceForm = form(deviceApproveSchema, async (data, issue) => {
  const event = getRequestEvent();
  if (!event) {
    throw new Error("Request event not available");
  }

  try {
    await callDeviceApprove(event, data.user_code, true);
    return { success: true };
  } catch (err) {
    console.error("Error approving device:", err);
    invalid(issue.user_code("The device code has expired or is no longer valid"));
  }
});

/**
 * Form handler to deny a device authorization request
 */
export const denyDeviceForm = form(deviceApproveSchema, async (data, issue) => {
  const event = getRequestEvent();
  if (!event) {
    throw new Error("Request event not available");
  }

  try {
    await callDeviceApprove(event, data.user_code, false);
    return { denied: true };
  } catch (err) {
    console.error("Error denying device:", err);
    invalid(issue.user_code("The device code has expired or is no longer valid"));
  }
});

// ============================================================================
// Consent Flow Form Function
// ============================================================================

const consentSchema = z.object({
  client_id: z.string().min(1),
  redirect_uri: z.string().min(1),
  scope: z.string().min(1),
  state: z.string().optional(),
  code_challenge: z.string().min(1),
  approved: z.string(),
  limit_to_24_hours: z.string().optional(),
});

/**
 * Form handler for OAuth consent approval/denial
 * Returns the redirect URL from the API response
 */
export const consentForm = form(consentSchema, async (data, issue) => {
  const event = getRequestEvent();
  if (!event) {
    throw new Error("Request event not available");
  }

  const { locals } = event;
  const { apiClient } = locals;

  // Build URL-encoded body for the OAuth authorize endpoint
  const body = new URLSearchParams();
  body.set("client_id", data.client_id);
  body.set("redirect_uri", data.redirect_uri);
  body.set("scope", data.scope);
  body.set("state", data.state ?? "");
  body.set("code_challenge", data.code_challenge);
  body.set("approved", data.approved);
  if (data.limit_to_24_hours) {
    body.set("limit_to_24_hours", data.limit_to_24_hours);
  }

  // Use fetch with redirect: "manual" to capture the redirect URL
  const response = await event.fetch(`${apiClient.baseUrl}/api/oauth/authorize`, {
    method: "POST",
    headers: {
      "Content-Type": "application/x-www-form-urlencoded",
    },
    body: body.toString(),
    redirect: "manual",
  });

  // The backend returns a 302 redirect
  if (response.status === 302 || response.status === 301) {
    const location = response.headers.get("Location");
    if (location) {
      throw redirect(302, location);
    }
  }

  if (response.status === 400) {
    const errorBody = await response.json().catch(() => null);
    invalid(issue.client_id(errorBody?.errorDescription ?? "Invalid authorization request."));
    return;
  }

  if (response.status === 401) {
    throw redirect(303, "/auth/login");
  }

  throw error(500, "Unexpected response from authorization server.");
});
