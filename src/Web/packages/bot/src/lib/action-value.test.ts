import { describe, it, expect } from "vitest";
import {
  encodeActionValue,
  decodeActionValue,
  encodeTenantKey,
} from "./action-value.js";

const TENANT = "11111111-1111-1111-1111-111111111111";
const OTHER_TENANT = "22222222-2222-2222-2222-222222222222";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

describe("card button values", () => {
  it("round-trips a tenant and an excursion", () => {
    const value = encodeActionValue({
      tenantId: TENANT,
      excursionId: EXCURSION,
    });

    expect(decodeActionValue(value)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: EXCURSION,
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

  it("names different tenants differently", () => {
    expect(encodeTenantKey(TENANT)).not.toBe(encodeTenantKey(OTHER_TENANT));
  });

  it("names a tenant the same however the value spelled it", () => {
    const legacy = decodeActionValue(`${TENANT.toUpperCase()}:${EXCURSION}`);

    expect(legacy.tenantKey).toBe(encodeTenantKey(TENANT));
  });

  it("reads a single segment as a tenant with no excursion", () => {
    expect(decodeActionValue(TENANT)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: null,
    });
  });

  it("reads the pre-budget compound value", () => {
    expect(decodeActionValue(`${TENANT}:${EXCURSION}`)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: EXCURSION,
    });
  });

  it.each([undefined, null, ""])("yields neither id for %p", (value) => {
    expect(decodeActionValue(value)).toEqual({
      tenantKey: null,
      excursionId: null,
    });
  });

  it("drops an excursion that names no tenant", () => {
    expect(decodeActionValue(`:${EXCURSION}`)).toEqual({
      tenantKey: null,
      excursionId: null,
    });
  });

  it("drops a second segment that is no excursion id", () => {
    expect(decodeActionValue(`${encodeTenantKey(TENANT)}:nonsense`)).toEqual({
      tenantKey: encodeTenantKey(TENANT),
      excursionId: null,
    });
  });

  it("keeps an unrecognised tenant segment so it matches no candidate", () => {
    expect(decodeActionValue("nonsense").tenantKey).toBe("nonsense");
  });
});
