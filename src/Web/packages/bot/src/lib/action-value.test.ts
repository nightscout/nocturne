import { describe, it, expect } from "vitest";
import { encodeActionValue, decodeActionValue } from "./action-value.js";

const TENANT = "11111111-1111-1111-1111-111111111111";
const EXCURSION = "33333333-3333-3333-3333-333333333333";

describe("card button values", () => {
  it("round-trips a tenant and an excursion", () => {
    const value = encodeActionValue({
      tenantId: TENANT,
      excursionId: EXCURSION,
    });

    expect(value).toBe(`${TENANT}:${EXCURSION}`);
    expect(decodeActionValue(value)).toEqual({
      tenantId: TENANT,
      excursionId: EXCURSION,
    });
  });

  it("reads a single segment as a tenant with no excursion", () => {
    expect(decodeActionValue(TENANT)).toEqual({
      tenantId: TENANT,
      excursionId: null,
    });
  });

  it.each([undefined, null, ""])("yields neither id for %p", (value) => {
    expect(decodeActionValue(value)).toEqual({
      tenantId: null,
      excursionId: null,
    });
  });

  it("drops an excursion that names no tenant", () => {
    expect(decodeActionValue(`:${EXCURSION}`)).toEqual({
      tenantId: null,
      excursionId: null,
    });
  });
});
