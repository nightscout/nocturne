import { describe, expect, it, vi } from "vitest";

const getMyTenants = vi.fn();

vi.mock("$app/server", () => ({
  getRequestEvent: () => ({
    locals: { isAuthenticated: true, apiClient: { myTenants: { getMyTenants } } },
    url: new URL("https://nocturne.run/settings"),
    request: new Request("https://nocturne.run/settings"),
  }),
  query: (fn: unknown) => fn,
}));

const { getCurrentTenantId } = await import("./current-tenant.remote");

describe("getCurrentTenantId", () => {
  it("asks only for the reachable tenants", async () => {
    getMyTenants.mockResolvedValue([{ id: "tenant-1", slug: "sleepy" }]);

    const id = await getCurrentTenantId();

    expect(getMyTenants).toHaveBeenCalledWith(true);
    expect(id).toBe("tenant-1");
  });
});
