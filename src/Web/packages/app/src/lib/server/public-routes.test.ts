import { describe, it, expect } from "vitest";
import { statusProbeRedirect } from "./public-routes";
import { SHARE_UNAVAILABLE_PATH } from "$lib/share-host";

describe("statusProbeRedirect", () => {
  /** A self-hosted install with no marketing site, on an ordinary tenant host. */
  const selfHosted = {
    isShareHost: false,
    recoveryMode: false,
    marketingUrl: undefined,
  };

  it("claims every status a share host cannot act on, ahead of the instance-wide ones", () => {
    // 404 an unresolvable token, 403 a suspended tenant, 503 an API that is itself unready. Each
    // would otherwise steer to /setup, /auth/recovery or the marketing site, none of which a
    // share host can do anything with.
    for (const apiStatus of [404, 403, 503]) {
      expect(
        statusProbeRedirect({
          ...selfHosted,
          isShareHost: true,
          recoveryMode: true,
          marketingUrl: "https://nocturne.run",
          apiStatus,
        }),
        String(apiStatus)
      ).toEqual({ location: SHARE_UNAVAILABLE_PATH, status: 303 });
    }
  });

  it("leaves every other host on its own destinations", () => {
    expect(statusProbeRedirect({ ...selfHosted, apiStatus: 503 })).toEqual({
      location: "/setup",
      status: 303,
    });
    expect(
      statusProbeRedirect({ ...selfHosted, apiStatus: 503, recoveryMode: true })
    ).toEqual({ location: "/auth/recovery", status: 303 });
    expect(statusProbeRedirect({ ...selfHosted, apiStatus: 404 })).toEqual({
      location: "/setup",
      status: 303,
    });
    expect(
      statusProbeRedirect({ ...selfHosted, apiStatus: 404, marketingUrl: "https://nocturne.run" })
    ).toEqual({ location: "https://nocturne.run", status: 302 });
  });

  it("sends nowhere on a status neither branch answers for", () => {
    // 403 is among them: only a share host reads it as a suspended tenant.
    for (const apiStatus of [401, 403, 500, undefined, null, "404"]) {
      expect(statusProbeRedirect({ ...selfHosted, apiStatus }), String(apiStatus)).toBeNull();
    }
  });

  it("sends a share host nowhere on a status that is not its dead end either", () => {
    for (const apiStatus of [401, 500, undefined, "404"]) {
      expect(
        statusProbeRedirect({ ...selfHosted, isShareHost: true, apiStatus }),
        String(apiStatus)
      ).toBeNull();
    }
  });
});
