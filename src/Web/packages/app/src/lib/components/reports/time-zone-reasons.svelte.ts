import { TimeZoneUnavailableReason } from "$lib/api";

export interface TimeZoneReasonCopy {
  cause: string;
  remedy?: string;
}

/** Built per call so wuchale reads them in the current locale. */
export function timeZoneReasonCopy(reason: TimeZoneUnavailableReason): TimeZoneReasonCopy {
  switch (reason) {
    case TimeZoneUnavailableReason.Share:
      return { cause: "A shared link does not show the time zone of this data." };
    case TimeZoneUnavailableReason.LookupFailed:
      return {
        cause: "The time zone of this data did not load.",
        remedy: "To try again, reload the report later.",
      };
    case TimeZoneUnavailableReason.Unrecognised:
      return {
        cause: "This server does not recognize the time zone in the profile settings.",
        remedy: "The owner of this data can correct the time zone in the profile settings.",
      };
    case TimeZoneUnavailableReason.NotConfigured:
      return {
        cause: "The profile settings do not have a time zone.",
        remedy: "The owner of this data can set a time zone in the profile settings.",
      };
  }
}
