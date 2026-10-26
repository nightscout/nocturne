/**
 * The calendar a report's days are cut on: the patient's, not the container's.
 *
 * The API resolves it (`GET /patient-record/timezone`): the patient record's
 * zone, else the active therapy profile's. That endpoint answers any caller who
 * may read glucose, reports or therapy, so a member who sees reports but not
 * therapy settings still gets the patient's calendar.
 *
 * A zone the runtime cannot resolve is treated as unset.
 *
 * Null means no source names one, and the caller chooses what to do about it.
 */
import type { RequestEvent } from "@sveltejs/kit";
import type { ApiClient } from "#lib/api/index.js";
import { isTimeZone } from "#lib/utils/date-range.js";
import { errorStatus } from "#lib/forms/submit-error.js";

/**
 * Read `fetch`, treating a refusal as "not named here" and nothing else.
 *
 * The patient timezone is not a shareable data category, so an anonymous
 * public-share viewer is refused it, and their reports must still render.
 *
 * Every other failure rethrows. A report that silently cut its days on UTC
 * because this lookup timed out would show a clinician the wrong day with
 * nothing on the page or in the logs to say so; failing the query says it.
 */
export async function readable<T>(fetch: () => Promise<T>): Promise<T | null> {
  try {
    return await fetch();
  } catch (err) {
    const status = errorStatus(err);
    if (status === 401 || status === 403) return null;
    throw err;
  }
}

async function resolve(apiClient: ApiClient): Promise<string | null> {
  const response = await readable(() =>
    apiClient.patientRecord.getPatientTimezone()
  );
  return isTimeZone(response?.timezone) ? response.timezone : null;
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
