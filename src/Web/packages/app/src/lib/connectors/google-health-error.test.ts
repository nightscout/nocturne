import { describe, expect, it } from "vitest";
import {
  describeGoogleHealthError,
  isGoogleHealthAlreadyRunningError,
} from "./google-health-error.svelte";

const messages: Record<string, string> = {
  reconnect_required: "Reconnect to Google.",
  already_running: "Another import is running.",
};
const known = (code: string) => (Object.hasOwn(messages, code) ? messages[code] : undefined);

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
    expect(message).toBe(
      "Reconnect to Google. Technical code: sync/reconnect_required · HTTP 400."
    );
  });

  it("recognizes only the exact coordination code", () => {
    expect(
      isGoogleHealthAlreadyRunningError({
        status: 400,
        body: { message: "already_running" },
      })
    ).toBe(true);
    expect(
      isGoogleHealthAlreadyRunningError({
        status: 409,
        body: { message: "A sync for connector 'googlehealth' is already running" },
      })
    ).toBe(false);
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
      expect(message).toContain("Technical code: readings/");
      expect(message).not.toMatch(
        /private|access_token|client_secret|toString|__proto__|Infinity/
      );
    }
  );
});
