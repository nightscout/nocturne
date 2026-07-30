import { describe, expect, it } from "vitest";
import { isInternalOnlyApiPath } from "./internal-only-api-paths";

describe("isInternalOnlyApiPath", () => {
  it.each([
    "/api/v4/platform/tls-authorize",
    "/API/v4/platform/tls-authorize",
    "/api/v4/Platform/TLS-Authorize",
    "/api/v4/platform/tls-authorize/",
    "/api/v4/platform/tls-authorize///",
  ])("refuses %s", (path) => {
    expect(isInternalOnlyApiPath(path)).toBe(true);
  });

  it.each([
    "/api/v4/platform/tls-authorized",
    "/api/v4/platform/tls-authorize/extra",
    "/api/v4/platform",
    "/api/v1/entries",
  ])("does not refuse %s", (path) => {
    expect(isInternalOnlyApiPath(path)).toBe(false);
  });
});
