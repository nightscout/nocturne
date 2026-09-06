import { describe, it, expect } from "vitest";
import {
  encodeActionValue,
  decodeActionValue,
  encodeTenantKey,
} from "./action-value.js";

const TENANT = "018f2a1b-3c4d-7000-8000-a1b2c3d4e5f6";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

/**
 * UUIDv7 ids minted in the same millisecond: identical for their leading 48
 * timestamp bits, and — the pair below — for their trailing three bytes too.
 * Only rand_b in between tells them apart.
 */
const SAME_MS_A = "018f2a1b-3c4d-7000-8000-aaaa00000001";
const SAME_MS_B = "018f2a1b-3c4d-7000-8000-bbbb00000001";

describe("card button values", () => {
  it("round-trips a tenant and an excursion", () => {
    const value = encodeActionValue({
      tenantId: TENANT,
      excursionId: EXCURSION,
    });

    expect(decodeActionValue(value)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: EXCURSION,
      unreadableExcursion: false,
    });
  });

  it.each([
    "018f2a1b-0000-7000-8000-000000000000",
    "ffffffff-ffff-ffff-ffff-ffffffffffff",
    "00000000-0000-0000-0000-000000000000",
  ])("round-trips the excursion %s exactly", (excursionId) => {
    const value = encodeActionValue({ tenantId: TENANT, excursionId });

    expect(decodeActionValue(value).excursionId).toBe(excursionId);
  });

  it("names two tenants minted in the same millisecond differently", () => {
    expect(encodeTenantKey(SAME_MS_A)).not.toBe(encodeTenantKey(SAME_MS_B));
  });

  it("names a tenant the same however the value spelled it", () => {
    const value = `${TENANT.toUpperCase()}:${EXCURSION}`;

    expect(decodeActionValue(value).tenantKey).toBe(encodeTenantKey(TENANT));
  });

  it("reads a single segment as a tenant with no excursion", () => {
    expect(decodeActionValue(TENANT)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: null,
      unreadableExcursion: false,
    });
  });

  it("reads a value carrying two full UUIDs", () => {
    expect(decodeActionValue(`${TENANT}:${EXCURSION}`)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: EXCURSION,
      unreadableExcursion: false,
    });
  });

  it.each([undefined, null, ""])("yields neither id for %p", (value) => {
    expect(decodeActionValue(value)).toEqual({
      tenantKey: null,
      excursionId: null,
      unreadableExcursion: false,
    });
  });

  it("drops an excursion that names no tenant", () => {
    expect(decodeActionValue(`:${EXCURSION}`)).toEqual({
      tenantKey: null,
      excursionId: null,
      unreadableExcursion: false,
    });
  });

  it.each(["nonsense", "*".repeat(22), "", ":"])(
    "marks the second segment %p unreadable",
    (segment) => {
      const value = `${encodeTenantKey(TENANT)}:${segment}`;

      expect(decodeActionValue(value)).toEqual({
        tenantKey: encodeTenantKey(TENANT),
        excursionId: null,
        unreadableExcursion: true,
      });
    },
  );

  it("addresses only a tenant when the value carries no separator", () => {
    expect(decodeActionValue(encodeTenantKey(TENANT))).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: null,
      unreadableExcursion: false,
    });
  });

  it("keeps an unrecognised tenant segment so it matches no candidate", () => {
    expect(decodeActionValue("nonsense").tenantKey).toBe("nonsense");
  });
});
