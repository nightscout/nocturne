import { OAuthScope } from "$lib/api/generated/nocturne-api-client";

export { OAuthScope } from "$lib/api/generated/nocturne-api-client";

export const OAUTH_SCOPE_DESCRIPTIONS: Readonly<Record<OAuthScope, string>> = {
  [OAuthScope.GlucoseRead]: "View glucose readings",
  [OAuthScope.GlucoseReadWrite]: "View and record glucose readings",
  [OAuthScope.TreatmentsRead]: "View treatments",
  [OAuthScope.TreatmentsReadWrite]: "View and record treatments",
  [OAuthScope.DevicesRead]: "View device status",
  [OAuthScope.DevicesReadWrite]: "View and update device status",
  [OAuthScope.TherapyRead]: "View therapy settings",
  [OAuthScope.TherapyReadWrite]: "View and update therapy settings",
  [OAuthScope.AlertsRead]: "View alerts",
  [OAuthScope.AlertsReadWrite]: "Manage alerts",
  [OAuthScope.ReportsRead]: "View reports and analytics",
  [OAuthScope.IdentityRead]: "View basic account info",
  [OAuthScope.SharingReadWrite]: "Manage sharing settings",
  [OAuthScope.HeartRateRead]: "View heart rate data",
  [OAuthScope.HeartRateReadWrite]: "View and record heart rate data",
  [OAuthScope.StepCountRead]: "View step count data",
  [OAuthScope.StepCountReadWrite]: "View and record step count data",
  [OAuthScope.StatisticsRead]: "View statistics",
  [OAuthScope.HealthRead]: "View all health data (read-only)",
  [OAuthScope.HealthReadWrite]: "View and update all health data",
  [OAuthScope.FullAccess]: "Full access including delete",
} as const;

export const OAUTH_AVAILABLE_SCOPES = Object.values(OAuthScope) as ReadonlyArray<OAuthScope>;

export function getOAuthScopeDescription(scope: OAuthScope): string;
export function getOAuthScopeDescription(scope: string): string;
export function getOAuthScopeDescription(scope: string): string {
  return (OAUTH_SCOPE_DESCRIPTIONS as Record<string, string>)[scope] ?? scope;
}
