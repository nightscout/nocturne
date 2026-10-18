// The Eversense care API as the Eversense NOW follower app reads it, serving synthetic data: one
// followed patient with a sensor glucose reading every five minutes, anchored to the time of the
// request. DevOnlyEversenseMock forwards `https://<host>/<path>` to `/eversense/<host>/<path>`, so
// the US and outside-US hosts are both served here and a spec can see which one was called.
//
// The follow list reports `IsTransmitterConnected: null`, as the live API does for a patient whose
// app has not reported a connection, and the history feed mixes in a calibration row, which the
// connector must not read as glucose.
//
// A refused password gets the body the Eversense 365 app parses (`IamTokenError`): 400
// invalid_grant, code 6008 and the remaining attempts; the third refusal locks the username with
// code 5005. The lockout's status is not known, so it is served as 400 too.

import type { Vendor, VendorReply, VendorRequest } from "./vendor.ts";

/** The password the fake account accepts. Test-only. */
export const EVERSENSE_PASSWORD = "e2e-fake-eversense-password";
export const EVERSENSE_USER_ID = 4242;
export const CALIBRATION_MGDL = 333;

const FIVE_MINUTES = 5 * 60 * 1000;
const AUTH_HOSTS = new Set(["usiamapi.eversensedms.com", "ousiamapialpha.eversensedms.com"]);
const DATA_HOSTS = new Set(["usapialpha.eversensedms.com", "ousalphaapiservices.eversensedms.com"]);
const tokens = new Set<string>();
const refusals = new Map<string, number>();
const MAX_ATTEMPTS = 3;

/** The care API's zone-less GMT form, `yyyy-MM-ddTHH:mm:ss`. */
const gmt = (t: number) => new Date(t).toISOString().slice(0, 19);
const parseGmt = (value: string | undefined) => (value ? Date.parse(value + "Z") : NaN);

/** Glucose on a fixed five-minute grid, so repeated syncs see identical readings. */
export function glucoseAt(t: number) {
  const step = t / FIVE_MINUTES;
  return Math.round(140 + 50 * Math.sin(step / 24) + 10 * Math.sin(step / 5));
}

export const newestReading = (now = Date.now()) => Math.floor(now / FIVE_MINUTES) * FIVE_MINUTES;

function authorised(request: VendorRequest) {
  const header = request.headers["authorization"] ?? "";
  return header.startsWith("Bearer ") && tokens.has(header.slice("Bearer ".length));
}

function token(request: VendorRequest): VendorReply {
  const form = new URLSearchParams(request.body);
  const username = form.get("username") ?? "";
  const refused = refusals.get(username) ?? 0;
  if (refused >= MAX_ATTEMPTS)
    return { status: 400, body: { error: "invalid_grant", error_description: "locked out", error_code: 5005 } };
  if (form.get("grant_type") !== "password" || form.get("password") !== EVERSENSE_PASSWORD) {
    refusals.set(username, refused + 1);
    return { status: 400, body: {
      error: "invalid_grant",
      error_description: `invalid credentials.RemainingAttempt:${MAX_ATTEMPTS - refused - 1}`,
      error_code: 6008,
    } };
  }
  const access = `e2e-eversense-${crypto.randomUUID()}`;
  tokens.add(access);
  return { status: 200, body: { access_token: access, expires_in: 3600, token_type: "Bearer" } };
}

function followingPatientList(): VendorReply {
  const newest = newestReading();
  return { status: 200, body: [{
    UserID: EVERSENSE_USER_ID,
    UserName: "e2e-patient@example.com",
    FirstName: "E2E",
    LastName: "Patient",
    CurrentGlucose: glucoseAt(newest),
    GlucoseTrend: 3,
    CGTime: gmt(newest),
    Units: 0,
    IsTransmitterConnected: null,
  }] };
}

function sensorGlucose(request: VendorRequest): VendorReply {
  if (Number(request.query["UserID"]) !== EVERSENSE_USER_ID) return { status: 200, body: [] };
  const start = parseGmt(request.query["startDate"]);
  const end = parseGmt(request.query["endDate"]);
  if (Number.isNaN(start) || Number.isNaN(end)) return { status: 400, body: { message: "invalid date" } };

  const rows = [];
  const newest = Math.min(newestReading(), end);
  for (let t = Math.ceil(start / FIVE_MINUTES) * FIVE_MINUTES; t <= newest; t += FIVE_MINUTES)
    rows.push({ EventTypeID: 1, EventSubTypeID: 0, Value: glucoseAt(t), EventDate: gmt(t) });
  // Outside the glucose curve's 80-200 mg/dL, so a spec can tell it apart from a reading.
  if (rows.length > 0)
    rows.push({ EventTypeID: 10, EventSubTypeID: 0, Value: CALIBRATION_MGDL, EventDate: rows[0]!.EventDate });
  return { status: 200, body: rows };
}

export const eversense: Vendor = {
  handle(request) {
    const [, host, ...rest] = request.path.split("/");
    const path = "/" + rest.join("/");
    if (host && AUTH_HOSTS.has(host) && request.method === "POST" && path === "/connect/token") return token(request);
    if (!host || !DATA_HOSTS.has(host)) return { status: 404, body: { error: `unknown host '${host}'` } };
    if (!authorised(request)) return { status: 401, body: { message: "Authorization has been denied for this request." } };
    if (path === "/api/care/GetFollowingPatientList") return followingPatientList();
    if (path === "/api/care/GetFollowingUserSensorGlucose") return sensorGlucose(request);
    return { status: 404, body: { error: `no route ${path}` } };
  },
};
