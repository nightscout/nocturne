import { describe, expect, it } from "vitest";
import { describeGoogleHealthError } from "./google-health-error";

const known = { reconnect_required: "Reconnect to Google." };

describe("Google Health diagnostics", () => {
  it("identifies a failed Nocturne session separately from Google consent", () => {
    const message = describeGoogleHealthError(
      { status: 401, body: { message: "private upstream response" } },
      "status",
      known
    );
    expect(message).toContain("Nocturne session is missing or has expired");
    expect(message).toContain("status/http_401");
    expect(message).toContain("HTTP 401");
    expect(message).not.toContain("private upstream response");
  });

  it("retains a recognized provider code with the failing action", () => {
    const message = describeGoogleHealthError(
      { status: 400, body: { message: "reconnect_required" } },
      "sync",
      known
    );
    expect(message).toContain("Reconnect to Google.");
    expect(message).toContain("sync/reconnect_required");
    expect(message).toContain("HTTP 400");
  });

  it.each([
    new Error("access_token=private-token client_secret=private-secret"),
    new TypeError("private health reading"),
    { status: 502, body: { message: "private provider body" } },
    { status: 400, body: { message: "toString" } },
    { status: Infinity, body: { message: "__proto__" } },
  ])(
    "never reveals unknown exceptions, provider data or inherited properties",
    (error) => {
      const message = describeGoogleHealthError(error, "readings", known);
      expect(message).toContain("Google Health data could not be retrieved");
      expect(message).toContain("Technical code: readings/");
      expect(message).not.toMatch(
        /private|access_token|client_secret|toString|__proto__|Infinity/
      );
    }
  );
});
