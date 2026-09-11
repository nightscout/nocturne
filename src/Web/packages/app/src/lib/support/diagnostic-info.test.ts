import { describe, it, expect } from "vitest";
import type { SupportDiagnosticsResponse } from "$api-clients";
import { buildDiagnosticInfo, type DiagnosticSources } from "./diagnostic-info";

const lastSync = new Date("2026-09-10T21:00:00Z");

const settings: SupportDiagnosticsResponse = {
  glucoseUnits: "mg/dl",
  timeFormat: "12",
  patientTimeZone: "America/Chicago",
  dataSourcePriority: "cgm",
  alertRuleCount: 7,
  connectors: [
    {
      name: "nightscout",
      isEnabled: true,
      isHealthy: false,
      lastSuccessfulSync: lastSync,
    },
  ],
};

const sources: DiagnosticSources = {
  userAgent: "Mozilla/5.0",
  screenSize: "1485x1569",
  route: "/settings/support",
  locale: "en-US",
  tenantSlug: "steph",
  cgmSource: "Dexcom G7",
  recentFailures: [
    { at: "2026-09-10T21:04:11Z", status: 403, route: "/reports", message: "Denied" },
  ],
  settings,
};

const allOff = {
  tenantSlug: false,
  cgmSource: false,
  recentErrors: false,
  settings: false,
};

function parse(json: string) {
  return JSON.parse(json) as Record<string, unknown>;
}

describe("buildDiagnosticInfo", () => {
  it("always carries the session basics", () => {
    expect(parse(buildDiagnosticInfo(sources, allOff))).toEqual({
      userAgent: "Mozilla/5.0",
      screenSize: "1485x1569",
      route: "/settings/support",
      locale: "en-US",
    });
  });

  it("attaches the real settings snapshot, not a placeholder", () => {
    const info = parse(buildDiagnosticInfo(sources, { ...allOff, settings: true }));

    expect(info.settings).toEqual({
      ...settings,
      connectors: [{ ...settings.connectors![0], lastSuccessfulSync: lastSync.toISOString() }],
    });
  });

  it("attaches the real failures, not a placeholder", () => {
    const info = parse(buildDiagnosticInfo(sources, { ...allOff, recentErrors: true }));

    expect(info.recentErrors).toEqual(sources.recentFailures);
  });

  it("omits settings that have not arrived rather than claiming they were sent", () => {
    const info = parse(
      buildDiagnosticInfo({ ...sources, settings: null }, { ...allOff, settings: true })
    );

    expect(info).not.toHaveProperty("settings");
  });

  it("omits an empty failure list rather than sending an empty array", () => {
    const info = parse(
      buildDiagnosticInfo({ ...sources, recentFailures: [] }, { ...allOff, recentErrors: true })
    );

    expect(info).not.toHaveProperty("recentErrors");
  });

  it("never emits the literal placeholder the toggles used to send", () => {
    const everythingOn = buildDiagnosticInfo(sources, {
      tenantSlug: true,
      cgmSource: true,
      recentErrors: true,
      settings: true,
    });

    expect(everythingOn).not.toContain('"included"');
  });

  it("leaves out what was not asked for", () => {
    const info = parse(buildDiagnosticInfo(sources, { ...allOff, tenantSlug: true }));

    expect(info.tenantSlug).toBe("steph");
    expect(info).not.toHaveProperty("cgmSource");
    expect(info).not.toHaveProperty("settings");
    expect(info).not.toHaveProperty("recentErrors");
  });

  it("says so when the reporter shared a CGM source but named none", () => {
    const info = parse(
      buildDiagnosticInfo({ ...sources, cgmSource: "" }, { ...allOff, cgmSource: true })
    );

    expect(info.cgmSource).toBe("not specified");
  });
});
