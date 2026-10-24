import {
  GoogleHealthCategoryKind,
  GoogleHealthFound,
  GoogleHealthSyncPhase,
  GoogleHealthTypeState,
  type GoogleHealthDataTypeStatus,
  type GoogleHealthStatus,
} from "$lib/api";
import { plural } from "$lib/utils/plural";
import { isGoogleHealthErrorCode, type GoogleHealthErrorCode } from "$lib/connectors/google-health-error-codes";

const errorMessages: Record<GoogleHealthErrorCode, string> = {
  configure_first: "Connect Google Health first.",
  platform_not_configured: "Google Health is not enabled on this Nocturne server.",
  client_changed:
    "This server's Google client has changed since you connected. Connect Google Health again. Nocturne keeps your imported data.",
  invalid_configuration: "Check the import start date.",
  invalid_callback:
    "Google rejected this server's redirect URI. Ask your Nocturne operator to check the Google Health setup.",
  invalid_client_credentials:
    "Google rejected this server's Google client. Ask your Nocturne operator to check the Google Health setup.",
  oauth_scope_configuration: "Google rejected a requested Health scope.",
  oauth_request_invalid: "Google rejected the OAuth request.",
  invalid_token_response: "Google did not return a usable session.",
  preview_required: "Review and save the available data types before importing.",
  no_types_selected: "Select at least one data type before starting an import.",
  disconnect_first: "Disconnect Google Health first.",
  account_mismatch:
    "This is a different Google account. Purge the old import before switching.",
  expired_signin: "The sign-in attempt expired. Start again.",
  offline_access_required:
    "Google did not grant background access. Revoke access and reconnect.",
  partial_consent: "Not every requested permission was granted.",
  permission_denied: "Google denied access.",
  reconnect_required: "Google access expired or was revoked. Reconnect.",
  rate_limited: "Google's request limit was reached. Nocturne will retry later.",
  google_unavailable:
    "Google is temporarily unavailable. Existing data was preserved.",
  already_running:
    "Another Google Health import is already running. The inventory will be available when it finishes. Refresh this page after that.",
  account_not_linked: "This Google account is not linked to Fitbit.",
  invalid_google_request: "Google rejected the data request.",
  preview_access_denied: "This account cannot access this API version.",
  google_resource_not_found: "This data type is unavailable for this account.",
  data_access_denied: "Google does not permit this data type to be read.",
  invalid_time_range:
    "Google rejected this date range. Choose a later start date.",
  invalid_filter_operator: "Google rejected Nocturne's time filter.",
  invalid_google_filter: "Google rejected the filter for this data type.",
  invalid_google_data_type: "Google did not recognize this data type.",
  invalid_source_family: "Google could not reconcile the sources.",
  invalid_google_response: "Google returned an incomplete response.",
  stored_google_configuration_unreadable:
    "The saved connection cannot be read. Configure it again.",
  unsupported_type: "The saved selection contains an unsupported type.",
  revoke_in_google: "Disconnected locally. Revoke the app in Google as well.",
  history_too_large:
    "Google exceeded the pagination safety limit. Try a later start date, or report the technical code if this persists.",
  invalid_google_data: "Google returned an unexpected data format.",
  pagination_failed: "Nocturne could not retrieve every page.",
  internal_sync:
    "Nocturne could not complete the import. Check the server log for the technical code and stage.",
  internal_sync_native_write:
    "Nocturne downloaded the data, but could not store it locally. Retry the same range. If the problem continues, use the technical stage from the server log.",
  activation_failed:
    "Google Health is connected, but Nocturne could not finish the set-up. Imports continue. Check the server log if this message stays.",
};

/** The sentence for a server error code, or a generic one for a code this page does not know. */
export function errorMessage(code: string): string {
  return knownErrorMessage(code) ?? "The import could not be completed.";
}

export function knownErrorMessage(code: string): string | undefined {
  return isGoogleHealthErrorCode(code) ? errorMessages[code] : undefined;
}

const notices = {
  importRunning:
    "The import is running in the background. You can leave this page and return later.",
  importCompleted: "Google Health import completed.",
  settingsSaved:
    "Import settings saved. No import has started. Automatic sync picks up the selection on its next scheduled run.",
  settingsSavedPaused:
    "Import settings saved. No import has started. Imports resume when your Nocturne operator turns Google Health back on.",
  settingsSavedOnly: "Import settings saved.",
  selectionPaused:
    "Import settings saved. Imports are paused because no data types are selected. Previously imported data is kept.",
  statusUnavailable: "Import status is temporarily unavailable. Retrying.",
  disconnected: "Google Health is disconnected. Your imported data has been kept.",
  alreadyRunning: errorMessages.already_running,
};

export type NoticeKey = keyof typeof notices;

export function noticeText(key: NoticeKey): string {
  return notices[key];
}

/** What a new import covers while no start date is set. */
export function lookbackSentence(days: number | undefined): string {
  return days
    ? plural(days, [
        "While the field is empty, a new import covers the last # day.",
        "While the field is empty, a new import covers the last # days.",
      ])
    : "While the field is empty, a new import covers the configured history lookback.";
}

const connectionOutcomes: Record<string, string> = {
  failed: "Google sign-in failed or was canceled.",
  provider_denied: "Google did not grant the requested read access.",
  no_session:
    "The Nocturne session was missing after the Google redirect. Sign in and reconnect in the same browser.",
};

/** Why the sign-in redirect came back without connecting, or undefined for any other value. */
export function connectionOutcome(outcome: string): string | undefined {
  return Object.hasOwn(connectionOutcomes, outcome)
    ? connectionOutcomes[outcome]
    : knownErrorMessage(outcome);
}

const categoryLabels: Record<GoogleHealthCategoryKind, string> = {
  [GoogleHealthCategoryKind.Vitals]: "Vitals",
  [GoogleHealthCategoryKind.Activity]: "Activity",
  [GoogleHealthCategoryKind.BodyMeasurement]: "Body measurement",
  [GoogleHealthCategoryKind.Nutrition]: "Nutrition",
  [GoogleHealthCategoryKind.Sleep]: "Sleep",
  [GoogleHealthCategoryKind.CycleTracking]: "Cycle tracking",
};

const dataTypeLabels: Record<string, string> = {
  steps: "Steps",
  heartrate: "Heart rate",
  weight: "Weight",
  sleep: "Sleep sessions and stages",
  activeenergyburned: "Active energy burned",
  totalcalories: "Total calories",
  distance: "Distance",
  floors: "Floors",
  exercise: "Workouts",
  bodyfat: "Body fat",
  height: "Height",
  nutritionlog: "Nutrition log",
  hydrationlog: "Hydration",
  bloodglucose: "Blood glucose",
  oxygensaturation: "Oxygen saturation",
  heartratevariability: "Heart rate variability",
  dailyrestingheartrate: "Resting heart rate",
  dailyoxygensaturation: "Daily oxygen saturation",
  dailyrespiratoryrate: "Daily respiratory rate",
  respiratoryratesleepsummary: "Sleep respiratory rate",
  corebodytemperature: "Core body temperature",
  menstrualperiod: "Menstrual period",
  ovulationtest: "Ovulation test",
  cervicalmucus: "Cervical mucus",
  sexualactivity: "Sexual activity",
};

export function categoryLabel(kind: GoogleHealthCategoryKind): string {
  return categoryLabels[kind];
}

// Dashes stripped so no key reads as a translatable message to Wuchale.
export function dataTypeLabel(type: string | null | undefined): string {
  return dataTypeLabels[(type ?? "").replaceAll("-", "")] ?? "Unknown data type";
}

const destinationLabels: Record<string, string> = {
  stepcounts: "Step history",
  heartrates: "Heart-rate history",
  bodyweights: "Weight history",
  sleepsessions: "Sleep history",
};

export function destinationLabel(destination: string | null | undefined): string {
  return destination
    ? (destinationLabels[destination.replaceAll("-", "")] ?? destination)
    : "No destination yet";
}

export function typeStateLabel(type: GoogleHealthDataTypeStatus): string {
  switch (type.state) {
    case GoogleHealthTypeState.ReadFailed:
      return `Read failed (${type.errorCode})`;
    case GoogleHealthTypeState.NoReadonlyScope:
      return "Google Health currently provides no read-only scope for this type.";
    case GoogleHealthTypeState.NotReadable:
      return "Not exposed as a readable Google Health API data type.";
    case GoogleHealthTypeState.NotYetSupported:
      return "Not yet supported by Nocturne";
    case GoogleHealthTypeState.PermissionNotGranted:
      return "Permission not granted";
    case GoogleHealthTypeState.NeedsAttention:
      return "Import needs attention";
    case GoogleHealthTypeState.Importing:
      return "Import enabled";
    case GoogleHealthTypeState.ImportingNoData:
      return "Import enabled. No data found.";
    case GoogleHealthTypeState.Available:
      return "Available to connect";
    case GoogleHealthTypeState.AvailableNoData:
      return "Supported, but no data found";
    default:
      return "Not scanned";
  }
}

export function foundLabel(type: GoogleHealthDataTypeStatus): string {
  switch (type.found) {
    case GoogleHealthFound.NoPermission:
      return "No permission";
    case GoogleHealthFound.ScanFailed:
      return "Scan failed";
    case GoogleHealthFound.Found:
      return `Yes (${type.count})`;
    case GoogleHealthFound.None:
      return "No";
    default:
      return "Not scanned";
  }
}

export function syncPhaseLabel(status: GoogleHealthStatus): string {
  switch (status.syncPhase) {
    case GoogleHealthSyncPhase.Queued:
      return "Waiting for the background worker";
    case GoogleHealthSyncPhase.RefreshingSession:
      return "Refreshing the Google session";
    case GoogleHealthSyncPhase.Reading:
      return status.syncDataType
        ? `Reading ${dataTypeLabel(status.syncDataType)}`
        : "Reading Google Health data";
    case GoogleHealthSyncPhase.Validating:
      return "Validating the downloaded data";
    case GoogleHealthSyncPhase.Integrating:
      return "Updating Nocturne health records";
    default:
      return "Preparing the import";
  }
}
