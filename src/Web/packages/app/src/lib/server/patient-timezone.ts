/**
 * The calendar a report's days are cut on: the patient's, not the container's.
 *
 * Source order mirrors the API's own `TherapySettingsResolver` — the patient
 * record is canonical, because a patient lives in one timezone however many
 * therapy profiles they have, and the per-profile `TherapySettings.Timezone` is
 * a legacy fallback that connector-imported profiles (Nightscout, Glooko) wrote
 * into before that move. Reading them the other way round lets a stale imported
 * `UTC` beat a correct patient record.
 *
 * The ORDER matches; the row selection does not. `getProfileSummary` returns
 * therapy settings across every profile ordered by timestamp descending with no
 * `<= now` bound, so `[0]` is the newest row anywhere — a future-dated or
 * inactive-profile row wins, where the API resolves the active profile as of
 * now. Only reachable for a patient whose record names no zone; fixing it needs
 * an endpoint that exposes the API's own `GetTimezoneAsync` rather than this
 * reconstruction.
 *
 * A zone the runtime cannot resolve is treated as unset, where the API takes
 * any non-empty `patient.Timezone` as given: the two disagree for a record
 * holding a garbage zone and a legacy row holding a real one.
 *
 * Null means no source names one, and the caller chooses what to do about it.
 */
import type { RequestEvent } from "@sveltejs/kit";
import type { ApiClient } from "$lib/api";
import { isTimeZone } from "$lib/utils/date-range";

/**
 * Read `fetch`, treating a refusal as "no zone named here" and nothing else.
 *
 * Neither the patient record nor therapy settings is a shareable data category,
 * so an anonymous public-share viewer is refused both, and their reports must
 * still render.
 *
 * Every other failure rethrows. A report that silently cut its days on UTC
 * because this lookup timed out would show a clinician the wrong day with
 * nothing on the page or in the logs to say so; failing the query says it.
 */
async function readable<T>(fetch: () => Promise<T>): Promise<T | null> {
  try {
    return await fetch();
  } catch (err) {
    const status = (err as { status?: number })?.status;
    if (status === 401 || status === 403) return null;
    throw err;
  }
}

async function resolve(apiClient: ApiClient): Promise<string | null> {
  const record = await readable(() =>
    apiClient.patientRecord.getPatientRecord()
  );
  if (isTimeZone(record?.timezone)) return record.timezone;

  // Only reached when the canonical source names nothing: `getProfileSummary` is
  // five sequential repository reads and is served `NoStore`, so it stays off the
  // path every request would otherwise take.
  const profile = await readable(() =>
    apiClient.profile.getProfileSummary(undefined, undefined)
  );
  const legacy = profile?.therapySettings?.[0]?.timezone;
  return isTimeZone(legacy) ? legacy : null;
}

/**
 * Resolved at most once per request. `resolveReportRange` runs for every query
 * a report page fires, and each one would otherwise repeat the lookup against
 * an endpoint the API explicitly declines to cache.
 */
const perRequest = new WeakMap<
  RequestEvent["locals"],
  Promise<string | null>
>();

/** The patient's IANA zone, or null when no source names one this runtime knows. */
export function resolvePatientTimeZone(
  locals: RequestEvent["locals"]
): Promise<string | null> {
  const cached = perRequest.get(locals);
  if (cached) return cached;

  const pending = resolve(locals.apiClient);
  perRequest.set(locals, pending);
  return pending;
}
