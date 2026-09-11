import type { SupportDiagnosticsResponse } from "$api-clients";
import type { ApiFailure } from "./api-failure-log";

/** What the support form knows about the session, before the user chooses what to share. */
export interface DiagnosticSources {
  userAgent: string;
  screenSize: string;
  route: string;
  locale: string;
  tenantSlug: string;
  cgmSource: string;
  recentFailures: readonly ApiFailure[];
  settings: SupportDiagnosticsResponse | null;
}

/** Which optional sections the user turned on. */
export interface DiagnosticToggles {
  tenantSlug: boolean;
  cgmSource: boolean;
  recentErrors: boolean;
  settings: boolean;
}

/**
 * Builds the diagnostic block attached to a support issue.
 *
 * A section appears only when it was both asked for and actually collected. The two are
 * separate on purpose: the toggles used to emit the literal string `"included"` whatever
 * happened, so a reporter could opt in to sharing diagnostics and send nothing.
 */
export function buildDiagnosticInfo(
  sources: DiagnosticSources,
  toggles: DiagnosticToggles
): string {
  const info: Record<string, unknown> = {
    userAgent: sources.userAgent,
    screenSize: sources.screenSize,
    route: sources.route,
    locale: sources.locale,
  };

  if (toggles.tenantSlug) info.tenantSlug = sources.tenantSlug;
  if (toggles.cgmSource) info.cgmSource = sources.cgmSource || "not specified";
  if (toggles.recentErrors && sources.recentFailures.length > 0) {
    info.recentErrors = sources.recentFailures;
  }
  if (toggles.settings && sources.settings) info.settings = sources.settings;

  return JSON.stringify(info, null, 2);
}
