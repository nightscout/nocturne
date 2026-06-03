/**
 * Platform-admin connector cursor-reset remote functions.
 *
 * Server-side wrappers around the cross-tenant connector admin endpoints
 * (`/api/v4/admin/connectors/...`). These are hand-authored (rather than
 * generated) because they call the API through SvelteKit's `event.fetch`,
 * which routes via the `/api` proxy so auth cookies, the instance key, and the
 * X-Forwarded-Host (tenant resolution) headers are all forwarded automatically.
 */

import { z } from "zod";
import { command, query, getRequestEvent } from "$app/server";
import { error } from "@sveltejs/kit";

/** Mirrors the backend SyncDataType enum (string-serialised). */
const syncDataTypeSchema = z.enum([
  "Glucose",
  "ManualBG",
  "Calibrations",
  "Boluses",
  "CarbIntake",
  "BGChecks",
  "BolusCalculations",
  "Notes",
  "DeviceEvents",
  "StateSpans",
  "Profiles",
  "DeviceStatus",
  "Activity",
  "Food",
]);

export type SyncDataType = z.infer<typeof syncDataTypeSchema>;

export interface TenantConnectorSummary {
  connectorName: string;
  isHealthy: boolean;
  lastSuccessfulSync: string | null;
  lastSyncAttempt: string | null;
  lastErrorMessage: string | null;
}

export interface TenantConnectorsDto {
  tenantId: string;
  tenantSlug: string;
  connectors: TenantConnectorSummary[];
}

export interface SyncResult {
  success: boolean;
  message: string;
  itemsSynced?: Record<string, number>;
  errors?: string[];
}

export interface ConnectorCursorResetResult {
  connectorName: string;
  result: SyncResult;
}

export interface TenantCursorResetResult {
  tenantId: string;
  tenantSlug: string;
  connectors: ConnectorCursorResetResult[];
}

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const { fetch } = getRequestEvent();
  const res = await fetch(path, init);
  if (!res.ok) {
    let detail = res.statusText;
    try {
      const body = await res.json();
      detail = body?.detail ?? body?.error ?? detail;
    } catch {
      // non-JSON body; keep statusText
    }
    throw error(res.status, detail);
  }
  return (await res.json()) as T;
}

/** Lists the connectors a target tenant has configured, with last-sync/health state. */
export const getTenantConnectors = query(z.string().uuid(), async (tenantId) =>
  apiFetch<TenantConnectorsDto>(`/api/v4/admin/connectors/${tenantId}`),
);

/**
 * Resets the cursor for every connector configured on the target tenant,
 * forcing a re-pull of history. Returns a per-connector SyncResult.
 */
export const resetTenantCursors = command(
  z.object({
    tenantId: z.string().uuid(),
    from: z.string().datetime().nullish(),
    dataTypes: z.array(syncDataTypeSchema).nullish(),
  }),
  async ({ tenantId, from, dataTypes }) =>
    apiFetch<TenantCursorResetResult>(`/api/v4/admin/connectors/${tenantId}/reset-cursors`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ from: from ?? null, dataTypes: dataTypes ?? null }),
    }),
);
