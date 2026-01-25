import { z } from "zod";
import { error } from "@sveltejs/kit";
import { getRequestEvent, query, command } from "$app/server";
import type { Entry, UserAlarmConfiguration } from "$lib/api";

const DebugSnapshotSchema = z
  .object({
    refreshToken: z.number().optional(),
  })
  .optional();

export const getAlertsDebugSnapshot = query(
  DebugSnapshotSchema,
  async () => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    try {
      const [alarmConfiguration, entries] = await Promise.all([
        apiClient.uiSettings.getAlarmConfiguration(),
        apiClient.entries.getEntries2(undefined, 1, undefined, "sgv", 1),
      ]);

      const latestEntry: Entry | null = entries?.[0] ?? null;

      return {
        alarmConfiguration: alarmConfiguration as UserAlarmConfiguration,
        latestEntry,
        fetchedAt: new Date().toISOString(),
      };
    } catch (err) {
      console.error("Failed to load alerts debug snapshot:", err);
      throw error(500, "Failed to load alerts debug snapshot");
    }
  }
);

const CreateFakeEntrySchema = z.object({
  sgv: z.preprocess(
    (value) => (typeof value === "string" ? Number(value) : value),
    z.number().min(20).max(600)
  ),
  device: z.string().optional(),
});

export const createFakeEntry = command(
  CreateFakeEntrySchema,
  async ({ sgv, device }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const now = new Date().toISOString();
    const entry: Entry = {
      sgv,
      mgdl: sgv,
      type: "sgv",
      device: device ?? "alerts-debug",
      notes: "Alerts debug entry",
      dateString: now,
    };

    const created = await apiClient.entries.createEntry(entry);

    return {
      ok: true,
      entry: created,
    };
  }
);

const TriggerAlarmSchema = z.object({
  profileId: z.string(),
});

export const triggerAlarmForProfile = command(
  TriggerAlarmSchema,
  async ({ profileId }) => {
    const { locals } = getRequestEvent();
    const { apiClient } = locals;

    const config = await apiClient.uiSettings.getAlarmConfiguration();
    const profile = config?.profiles?.find((item) => item.id === profileId);

    if (!profile) {
      return { ok: false, reason: "missing_profile" } as const;
    }

    const threshold = profile.threshold;
    if (threshold === null || threshold === undefined) {
      return { ok: false, reason: "missing_threshold" } as const;
    }

    let sgv: number | null = null;
    switch (profile.alarmType) {
      case "Low":
      case "UrgentLow":
      case "ForecastLow":
        sgv = Math.max(20, threshold - 5);
        break;
      case "High":
      case "UrgentHigh":
        sgv = threshold + 5;
        break;
      default:
        return { ok: false, reason: "unsupported_type" } as const;
    }

    const now = new Date().toISOString();
    const entry: Entry = {
      sgv,
      mgdl: sgv,
      type: "sgv",
      device: "alerts-debug",
      notes: `Alerts debug trigger for ${profile.alarmType}`,
      dateString: now,
    };

    const created = await apiClient.entries.createEntry(entry);

    return {
      ok: true,
      entry: created,
      usedSgv: sgv,
      alarmType: profile.alarmType,
      profileId: profile.id,
    };
  }
);
