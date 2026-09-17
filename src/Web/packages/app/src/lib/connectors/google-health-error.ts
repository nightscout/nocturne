import { errorMessage, errorStatus } from "$lib/forms/submit-error";

const operations = {
  status: "The connection status could not be retrieved.",
  readings: "The available Google Health data could not be retrieved.",
  save: "The Google settings could not be saved.",
  signin: "Google sign-in could not be started.",
  sync: "The Google import could not be completed.",
  disconnect: "Google could not be disconnected.",
  purge: "The imported Google data could not be deleted.",
};

export type GoogleHealthOperation = keyof typeof operations;

export function describeGoogleHealthError(
  error: unknown,
  operation: GoogleHealthOperation,
  knownErrors: Record<string, string>
): string {
  const status = errorStatus(error);
  const http =
    status !== undefined &&
    Number.isInteger(status) &&
    status >= 400 &&
    status <= 599
      ? status
      : undefined;
  const reason = errorMessage(error);
  const known = reason !== undefined && Object.hasOwn(knownErrors, reason);
  const code = known ? reason : http ? `http_${http}` : "page_or_network_error";
  const explanation = known
    ? knownErrors[reason]
    : http === 401
      ? "Your Nocturne session is missing or has expired. Sign in to Nocturne and reload this page."
      : http === 403
        ? "Your Nocturne account cannot access these settings. Use an administrator account with tenant settings permission."
        : http === 404
          ? "This feature is missing from the installed version. Check that Nocturne is up to date."
          : http === 429
            ? "Too many requests were made. Try again in a few minutes."
            : http && http >= 500
              ? "Nocturne could not process the request. Check the server log for this attempt."
              : "A page or connection error occurred. Reload the page; if this continues, report the technical code.";

  // Only fixed messages and recognized codes may leave the error boundary.
  return `${operations[operation]} ${explanation} Technical code: ${operation}/${code}${http ? ` · HTTP ${http}` : ""}.`;
}
