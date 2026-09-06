/**
 * Builds the diagnostic payload the support page copies or downloads.
 *
 * Every field a toggle claims to control must be omitted when that toggle is
 * off — the payload is shared with support, so the privacy control has to be
 * real. Nothing here reads glucose data, credentials, or account identifiers.
 */

export interface DiagnosticDevice {
  userAgent?: string;
  platform?: string;
  screenSize?: string;
}

export interface DiagnosticBuild {
  /** Server version string from /api/v1/status. */
  version?: string | null;
  /** Commit the running server was built from. */
  head?: string | null;
  /** Build timestamp. */
  build?: string | Date | null;
}

export interface DiagnosticReportInput {
  timestamp: string;
  build?: DiagnosticBuild | null;
  includeDeviceInfo: boolean;
  device?: DiagnosticDevice;
  additionalDetails?: string;
}

/** Placeholder values the status endpoint uses when the build is unknown. */
const UNKNOWN_HEADS = ["unknown", "nocturne-dev"];

export function buildDiagnosticReport(input: DiagnosticReportInput): string {
  const report: Record<string, unknown> = { timestamp: input.timestamp };

  const version = input.build?.version?.trim();
  if (version) {
    report.version = version;
  }

  const head = input.build?.head?.trim();
  if (head && !UNKNOWN_HEADS.includes(head)) {
    report.commit = head;
  }

  const build = input.build?.build;
  if (build) {
    report.built = build instanceof Date ? build.toISOString() : build;
  }

  if (input.includeDeviceInfo) {
    report.device = {
      userAgent: input.device?.userAgent ?? "unknown",
      platform: input.device?.platform ?? "unknown",
      screenSize: input.device?.screenSize ?? "unknown",
    };
  }

  const details = input.additionalDetails?.trim();
  if (details) {
    report.additionalDetails = details;
  }

  return JSON.stringify(report, null, 2);
}

/** Reads the current browser environment, or undefined outside the browser. */
export function readDiagnosticDevice(): DiagnosticDevice | undefined {
  if (typeof navigator === "undefined" || typeof window === "undefined") {
    return undefined;
  }
  return {
    userAgent: navigator.userAgent,
    platform: navigator.platform,
    screenSize: `${window.innerWidth}x${window.innerHeight}`,
  };
}
