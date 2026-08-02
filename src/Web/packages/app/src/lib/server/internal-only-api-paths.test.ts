import { describe, expect, it } from "vitest";
import { isInternalOnlyApiPath } from "./internal-only-api-paths";

describe("isInternalOnlyApiPath", () => {
  it.each([
    "/api/v4/platform/tls-authorize",
    "/API/v4/platform/tls-authorize",
    "/api/v4/Platform/TLS-Authorize",
    "/api/v4/platform/tls-authorize/",
    "/api/v4/platform/tls-authorize///",
    "/api/v4/platform/%74ls-authorize",
    "/api/v4/platform/tls-%61uthorize",
    "/%61pi/v4/platform/tls-authorize",
    "/api/v4/platform/%74%6C%73-authorize/",
    "/API/v4/PLATFORM/%54LS-AUTHORIZE",
    // Rejoined as a separator rather than kept literal. Kestrel 404s on an encoded slash, so
    // refusing it is the safe side of the mismatch.
    "/api/v4/platform%2Ftls-authorize",
  ])("refuses %s", (path) => {
    expect(isInternalOnlyApiPath(path)).toBe(true);
  });

  it.each([
    "/api/v4/platform/tls-authorized",
    "/api/v4/platform/tls-authorize/extra",
    "/api/v4/platform",
    "/api/v1/entries",
    "/api/v4/platform/%zz",
  ])("does not refuse %s", (path) => {
    expect(isInternalOnlyApiPath(path)).toBe(false);
  });
});
