import { createHash } from "crypto";
import { afterEach, beforeEach, describe, expect, it } from "vitest";
import {
  INSTANCE_KEY_HEADER,
  INSTANCE_SERVICE_HEADER,
  getHashedInstanceKey,
  isTrustedInstanceRequest,
} from "./instance-key";

const INSTANCE_KEY = "s3cret-instance-key";
const DIGEST = createHash("sha256").update(INSTANCE_KEY).digest("hex");

function makeRequest(headers: Record<string, string>): Request {
  return new Request("http://web.internal/api/v4/bot/dispatch", {
    method: "POST",
    headers,
  });
}

describe("isTrustedInstanceRequest", () => {
  const previous = process.env.INSTANCE_KEY;

  beforeEach(() => {
    process.env.INSTANCE_KEY = INSTANCE_KEY;
  });

  afterEach(() => {
    if (previous === undefined) delete process.env.INSTANCE_KEY;
    else process.env.INSTANCE_KEY = previous;
  });

  it("hashes the configured instance key as lowercase sha256 hex", () => {
    expect(getHashedInstanceKey()).toBe(DIGEST);
  });

  it("accepts the digest plus a service marker", () => {
    const request = makeRequest({
      [INSTANCE_KEY_HEADER]: DIGEST,
      [INSTANCE_SERVICE_HEADER]: "nocturne-api",
    });

    expect(isTrustedInstanceRequest(request)).toBe(true);
  });

  it("accepts an uppercase digest", () => {
    const request = makeRequest({
      [INSTANCE_KEY_HEADER]: DIGEST.toUpperCase(),
      [INSTANCE_SERVICE_HEADER]: "nocturne-api",
    });

    expect(isTrustedInstanceRequest(request)).toBe(true);
  });

  it("rejects a request with no instance key header", () => {
    expect(
      isTrustedInstanceRequest(
        makeRequest({ [INSTANCE_SERVICE_HEADER]: "nocturne-api" }),
      ),
    ).toBe(false);
    expect(isTrustedInstanceRequest(makeRequest({}))).toBe(false);
  });

  it("rejects a wrong digest, including one of a different length", () => {
    const wrong = createHash("sha256").update("not-the-key").digest("hex");

    expect(
      isTrustedInstanceRequest(
        makeRequest({
          [INSTANCE_KEY_HEADER]: wrong,
          [INSTANCE_SERVICE_HEADER]: "nocturne-api",
        }),
      ),
    ).toBe(false);
    expect(
      isTrustedInstanceRequest(
        makeRequest({
          [INSTANCE_KEY_HEADER]: DIGEST.slice(0, 32),
          [INSTANCE_SERVICE_HEADER]: "nocturne-api",
        }),
      ),
    ).toBe(false);
  });

  it("rejects the raw instance key presented instead of its digest", () => {
    expect(
      isTrustedInstanceRequest(
        makeRequest({
          [INSTANCE_KEY_HEADER]: INSTANCE_KEY,
          [INSTANCE_SERVICE_HEADER]: "nocturne-api",
        }),
      ),
    ).toBe(false);
  });

  it("rejects a valid digest with no service marker", () => {
    expect(
      isTrustedInstanceRequest(makeRequest({ [INSTANCE_KEY_HEADER]: DIGEST })),
    ).toBe(false);
  });

  it("rejects everything when no instance key is configured", () => {
    delete process.env.INSTANCE_KEY;

    expect(
      isTrustedInstanceRequest(
        makeRequest({
          [INSTANCE_KEY_HEADER]: DIGEST,
          [INSTANCE_SERVICE_HEADER]: "nocturne-api",
        }),
      ),
    ).toBe(false);
    expect(
      isTrustedInstanceRequest(
        makeRequest({
          [INSTANCE_KEY_HEADER]: "",
          [INSTANCE_SERVICE_HEADER]: "nocturne-api",
        }),
      ),
    ).toBe(false);
  });
});
