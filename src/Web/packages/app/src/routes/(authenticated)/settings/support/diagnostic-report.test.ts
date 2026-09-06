import { describe, it, expect } from "vitest";
import { buildDiagnosticReport } from "./diagnostic-report";

const device = {
  userAgent: "Mozilla/5.0 (Test)",
  platform: "TestPlatform",
  screenSize: "1280x720",
};

const timestamp = "2026-07-25T00:00:00.000Z";

describe("buildDiagnosticReport", () => {
  it("omits device information when the toggle is off", () => {
    const report = JSON.parse(
      buildDiagnosticReport({ timestamp, includeDeviceInfo: false, device })
    );
    expect(report.device).toBeUndefined();
    expect(JSON.stringify(report)).not.toContain("TestPlatform");
    expect(JSON.stringify(report)).not.toContain("1280x720");
    expect(JSON.stringify(report)).not.toContain("Mozilla");
  });

  it("includes device information when the toggle is on", () => {
    const report = JSON.parse(
      buildDiagnosticReport({ timestamp, includeDeviceInfo: true, device })
    );
    expect(report.device).toEqual(device);
  });

  it("reports the real server version, commit and build", () => {
    const report = JSON.parse(
      buildDiagnosticReport({
        timestamp,
        includeDeviceInfo: false,
        build: { version: "4.2.0", head: "abc1234def", build: "2026-07-20T10:00:00.000Z" },
      })
    );
    expect(report.version).toBe("4.2.0");
    expect(report.commit).toBe("abc1234def");
    expect(report.built).toBe("2026-07-20T10:00:00.000Z");
  });

  it("omits the commit when the server reports a placeholder", () => {
    for (const head of ["unknown", "nocturne-dev"]) {
      const report = JSON.parse(
        buildDiagnosticReport({ timestamp, includeDeviceInfo: false, build: { head } })
      );
      expect(report.commit, head).toBeUndefined();
    }
  });

  it("omits version fields entirely when status is unavailable", () => {
    const report = JSON.parse(
      buildDiagnosticReport({ timestamp, includeDeviceInfo: false, build: null })
    );
    expect(report).toEqual({ timestamp });
  });

  it("includes additional details only when the user typed something", () => {
    expect(
      JSON.parse(
        buildDiagnosticReport({
          timestamp,
          includeDeviceInfo: false,
          additionalDetails: "   ",
        })
      ).additionalDetails
    ).toBeUndefined();

    expect(
      JSON.parse(
        buildDiagnosticReport({
          timestamp,
          includeDeviceInfo: false,
          additionalDetails: "Chart was blank after 3am",
        })
      ).additionalDetails
    ).toBe("Chart was blank after 3am");
  });

  it("never emits booleans in place of the values they gate", () => {
    const report = buildDiagnosticReport({
      timestamp,
      includeDeviceInfo: true,
      device,
    });
    expect(report).not.toContain("true");
    expect(report).not.toContain("false");
  });
});
