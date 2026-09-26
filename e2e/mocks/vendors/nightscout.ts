// A legacy Nightscout v1 API serving synthetic data: a CGM reading every five minutes for the
// last 48 hours and a handful of treatments, anchored to the time of the request so a
// connector's lookback window always finds them. The record shapes follow what an xDrip+ /
// Loop uploader writes; the values are generated.
//
// Queries honour what the Nightscout connector sends: `count`, `find[date][$gte|$lte]` on
// entries and `find[created_at][$gte|$lte]` (string comparison, as Mongo does) elsewhere.

import type { Vendor, VendorReply, VendorRequest } from "./vendor.ts";

/** The API secret the fake instance accepts. Test-only. */
export const NIGHTSCOUT_API_SECRET = "e2e-fake-nightscout-secret";
/**
 * The `api-secret` header a client sends for it: Nightscout's protocol mandates the hex SHA-1 of
 * the secret. Written out, not computed, so no weak hash is ever run over secret material here.
 */
export const NIGHTSCOUT_API_SECRET_HEADER = "ea8d7a4d53ada39ae62c9a994def88d4245ad5e4";

export const DEVICE = "e2e-fake-nightscout";
const FIVE_MINUTES = 5 * 60 * 1000;
const HISTORY = 48 * 60 * 60 * 1000;

/** Readings on a fixed five-minute grid, so repeated syncs see identical records. */
export function entries(now = Date.now()) {
  const newest = Math.floor(now / FIVE_MINUTES) * FIVE_MINUTES;
  const out = [];
  for (let t = newest; t > newest - HISTORY; t -= FIVE_MINUTES) {
    const step = t / FIVE_MINUTES;
    const sgv = Math.round(140 + 50 * Math.sin(step / 24) + 10 * Math.sin(step / 5));
    out.push({
      _id: `e2e-entry-${t}`,
      date: t,
      mills: t,
      dateString: new Date(t).toISOString(),
      sysTime: new Date(t).toISOString(),
      sgv,
      mgdl: sgv,
      direction: "Flat",
      trend: 4,
      type: "sgv",
      device: DEVICE,
      noise: 1,
      utcOffset: 0,
    });
  }
  return out;
}

export function treatments(now = Date.now()) {
  const hour = 60 * 60 * 1000;
  const base = Math.floor(now / hour) * hour;
  const at = (hoursAgo: number) => {
    const t = base - hoursAgo * hour;
    return { mills: t, created_at: new Date(t).toISOString(), srvModified: t, srvCreated: t };
  };
  return [
    { _id: "e2e-t-meal", identifier: "e2e-t-meal", eventType: "Meal Bolus", carbs: 45, insulin: 3.5, ...at(3), enteredBy: DEVICE },
    { _id: "e2e-t-corr", identifier: "e2e-t-corr", eventType: "Correction Bolus", insulin: 0.8, ...at(6), enteredBy: DEVICE },
    { _id: "e2e-t-carb", identifier: "e2e-t-carb", eventType: "Carb Correction", carbs: 15, ...at(9), enteredBy: DEVICE },
    { _id: "e2e-t-temp", identifier: "e2e-t-temp", eventType: "Temp Basal", duration: 30, absolute: 1.2, rate: 1.2, temp: "absolute", ...at(12), enteredBy: DEVICE },
    { _id: "e2e-t-site", identifier: "e2e-t-site", eventType: "Site Change", ...at(20), enteredBy: DEVICE },
  ].sort((a, b) => b.mills - a.mills);
}

const profile = [
  {
    _id: "e2e-profile",
    defaultProfile: "Default",
    startDate: "2026-01-01T00:00:00.000Z",
    mills: Date.parse("2026-01-01T00:00:00.000Z"),
    created_at: "2026-01-01T00:00:00.000Z",
    units: "mg/dl",
    store: {
      Default: {
        dia: 4,
        carbratio: [{ time: "00:00", value: 10, timeAsSeconds: 0 }],
        carbs_hr: 20,
        delay: 20,
        sens: [{ time: "00:00", value: 50, timeAsSeconds: 0 }],
        timezone: "UTC",
        basal: [{ time: "00:00", value: 0.8, timeAsSeconds: 0 }],
        target_low: [{ time: "00:00", value: 90, timeAsSeconds: 0 }],
        target_high: [{ time: "00:00", value: 140, timeAsSeconds: 0 }],
        startDate: "1970-01-01T00:00:00.000Z",
        units: "mg/dl",
      },
    },
  },
];

function status() {
  return {
    status: "ok",
    name: "nightscout",
    version: "15.0.2",
    serverTime: new Date().toISOString(),
    serverTimeEpoch: Date.now(),
    apiEnabled: true,
    careportalEnabled: true,
    settings: { units: "mg/dl", thresholds: { bgHigh: 260, bgTargetTop: 180, bgTargetBottom: 80, bgLow: 55 } },
  };
}

function limit<T>(rows: T[], query: Record<string, string>): T[] {
  const count = Number(query.count ?? 10);
  return rows.slice(0, Number.isFinite(count) && count > 0 ? count : 10);
}

function byDate(query: Record<string, string>) {
  const gte = query["find[date][$gte]"];
  const lte = query["find[date][$lte]"];
  return (row: { date: number }) =>
    (gte === undefined || row.date >= Number(gte)) && (lte === undefined || row.date <= Number(lte));
}

function byCreatedAt(query: Record<string, string>) {
  const gte = query["find[created_at][$gte]"];
  const lte = query["find[created_at][$lte]"];
  return (row: { created_at: string }) =>
    (gte === undefined || row.created_at >= gte) && (lte === undefined || row.created_at <= lte);
}

const ok = (body: unknown): VendorReply => ({ status: 200, body });

export const nightscout: Vendor = {
  handle(request: VendorRequest): VendorReply {
    if (request.path === "/api/v1/status.json") return ok(status());
    if (request.headers["api-secret"]?.toLowerCase() !== NIGHTSCOUT_API_SECRET_HEADER) {
      return { status: 401, body: { status: 401, message: "Unauthorized" } };
    }

    switch (request.path) {
      case "/api/v1/entries.json":
      case "/api/v1/entries/sgv.json":
        return ok(limit(entries().filter(byDate(request.query)), request.query));
      case "/api/v1/treatments.json":
        return ok(limit(treatments().filter(byCreatedAt(request.query)), request.query));
      case "/api/v1/devicestatus.json":
      case "/api/v1/food.json":
      case "/api/v1/activity.json":
        return ok([]);
      case "/api/v1/profile.json":
        return ok(profile);
      default:
        return { status: 404, body: { status: 404, message: `fake nightscout has no ${request.path}` } };
    }
  },
};
