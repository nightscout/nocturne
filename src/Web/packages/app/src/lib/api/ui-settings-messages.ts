/**
 * What a failed settings read tells the user.
 *
 * It arrives by two paths: a server failure is sanitized to it by `getUiSettings`,
 * while a transport failure is suppressed as client-written and the caller's
 * `remoteErrorMessage` fallback stands in. The card has to read the same
 * whichever happened, so both paths spell it once here.
 */
export const SETTINGS_LOAD_FAILED =
  "We couldn't load your settings. Refresh the page to try again.";
