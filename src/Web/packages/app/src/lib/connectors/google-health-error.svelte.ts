import { errorMessage, errorStatus } from "#lib/forms/submit-error.js";

export type GoogleHealthOperation =
  | "status"
  | "readings"
  | "save"
  | "signin"
  | "sync"
  | "disconnect"
  | "purge";

/**
 * A scheduled connector run can already own the tenant-wide sync slot when a
 * user presses Sync now. That is a normal coordination result, not a failed
 * Google request.
 */
export function isGoogleHealthAlreadyRunningError(error: unknown): boolean {
  return errorMessage(error) === "already_running";
}

function httpStatus(error: unknown): number | undefined {
  const status = errorStatus(error);
  return status !== undefined && Number.isInteger(status) && status >= 400 && status <= 599
    ? status
    : undefined;
}

function explain(http: number | undefined): string {
  if (http === 401)
    return "Your Nocturne session is missing or has expired. Sign in to Nocturne and reload this page.";
  if (http === 403)
    return "Your Nocturne account cannot access these settings. Use an administrator account with tenant settings permission.";
  if (http === 404)
    return "This feature is missing from the installed version. Check that Nocturne is up to date.";
  if (http === 429) return "Too many requests were made. Try again in a few minutes.";
  if (http !== undefined && http >= 500)
    return "Nocturne could not process the request. Check the server log for this attempt.";
  return "A page or connection error occurred. Reload the page. If this continues, report the technical code.";
}

/**
 * One translated sentence for a failed request, followed by the technical code
 * support matches it on. Only recognised codes and fixed text leave here: an
 * unrecognised server message may carry provider data.
 */
export function describeGoogleHealthError(
  error: unknown,
  operation: GoogleHealthOperation,
  knownError: (code: string) => string | undefined
): string {
  const http = httpStatus(error);
  const reason = errorMessage(error);
  const known = reason === undefined ? undefined : knownError(reason);
  const code = known !== undefined ? reason : http ? `http_${http}` : "page_or_network_error";
  const message = known ?? explain(http);
  const technical = http ? `${operation}/${code} · HTTP ${http}` : `${operation}/${code}`;
  return `${message} Technical code: ${technical}.`;
}
