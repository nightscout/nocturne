/**
 * Remote functions for glucose processing settings.
 *
 * Manual implementation — will be replaced by generated remote functions
 * once the API project builds cleanly and NSwag regenerates.
 */
import { getRequestEvent, query, command } from "$app/server";
import { error, redirect } from "@sveltejs/kit";
import { z } from "zod";

// ── Schemas ──────────────────────────────────────────────────────────

const setPreferenceSchema = z.object({
  preferredGlucoseProcessing: z.string().nullable(),
});

const sourceDefaultRuleSchema = z.object({
  match: z.string(),
  field: z.string(),
  processing: z.string(),
});

const setSourceDefaultsSchema = z.object({
  rules: z.array(sourceDefaultRuleSchema),
});

// ── Types ────────────────────────────────────────────────────────────

export interface GlucoseProcessingPreferenceResponse {
  preferredGlucoseProcessing: string | null;
}

export interface GlucoseProcessingSourceDefault {
  match: string;
  field: string;
  processing: string;
}

export interface GlucoseProcessingSourceDefaultsResponse {
  rules: GlucoseProcessingSourceDefault[];
}

// ── Helpers ──────────────────────────────────────────────────────────

function handleError(err: unknown, operation: string): never {
  const status = (err as any)?.status;
  if (status === 401) {
    const { url } = getRequestEvent();
    throw redirect(
      302,
      `/auth/login?returnUrl=${encodeURIComponent(url.pathname + url.search)}`
    );
  }
  if (status === 403) throw error(403, "Forbidden");
  console.error(`Error in glucoseProcessingSettings.${operation}:`, err);
  throw error(500, `Failed to ${operation}`);
}

async function apiFetch(path: string, init?: RequestInit): Promise<Response> {
  const event = getRequestEvent();
  const res = await event.fetch(`/api/v4/settings/glucose-processing${path}`, {
    ...init,
    headers: {
      "Content-Type": "application/json",
      ...init?.headers,
    },
  });
  if (!res.ok) {
    throw Object.assign(new Error(res.statusText), { status: res.status });
  }
  return res;
}

// ── Queries ──────────────────────────────────────────────────────────

export const getPreference = query(
  async (): Promise<GlucoseProcessingPreferenceResponse> => {
    try {
      const res = await apiFetch("/preference");
      return await res.json();
    } catch (err) {
      return handleError(err, "getPreference");
    }
  }
);

export const getSourceDefaults = query(
  async (): Promise<GlucoseProcessingSourceDefaultsResponse> => {
    try {
      const res = await apiFetch("/source-defaults");
      return await res.json();
    } catch (err) {
      return handleError(err, "getSourceDefaults");
    }
  }
);

// ── Commands ─────────────────────────────────────────────────────────

export const setPreference = command(
  setPreferenceSchema,
  async (request) => {
    try {
      await apiFetch("/preference", {
        method: "PUT",
        body: JSON.stringify(request),
      });
    } catch (err) {
      return handleError(err, "setPreference");
    }
  }
);

export const setSourceDefaults = command(
  setSourceDefaultsSchema,
  async (request) => {
    try {
      await apiFetch("/source-defaults", {
        method: "PUT",
        body: JSON.stringify(request),
      });
    } catch (err) {
      return handleError(err, "setSourceDefaults");
    }
  }
);
