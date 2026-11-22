import { createRawSnippet } from "svelte";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { SHELL_REVALIDATE_INTERVAL_MS } from "#lib/constants/shell-data.js";

const mounted = vi.hoisted(() => ({ alerts: 0, refresh: [] as unknown[][] }));

vi.mock("$app/navigation", () => ({ invalidate: async () => {} }));
vi.mock("#lib/components/alerts/AlertSurfaces.svelte", () => ({
  default: () => {
    mounted.alerts++;
  },
}));
vi.mock("#lib/utils/shell-session.js", () => ({ createShellRevalidator: () => "revalidator" }));
vi.mock("#lib/utils/refresh-on-return.svelte.js", () => ({
  refreshOnVisible: (...args: unknown[]) => {
    mounted.refresh.push(args);
  },
}));

import WallLayout from "./+layout@.svelte";

const children = createRawSnippet(() => ({ render: () => "<p>The wall</p>" }));

function layoutData(overrides: Record<string, unknown>) {
  return { tenantless: false, effectivePermissions: ["alerts.read"], ...overrides };
}

beforeEach(() => {
  mounted.alerts = 0;
  mounted.refresh = [];
});

describe("people wall layout", () => {
  it("keeps the viewer's own alerts running", async () => {
    render(WallLayout, { props: { data: layoutData({}) as never, children } });
    await expect.element(page.getByText("The wall")).toBeVisible();

    expect(mounted.alerts).toBe(1);
  });

  it("mounts no alert surface for a viewer who cannot read alerts", async () => {
    render(WallLayout, {
      props: { data: layoutData({ effectivePermissions: ["glucose.read"] }) as never, children },
    });
    await expect.element(page.getByText("The wall")).toBeVisible();

    expect(mounted.alerts).toBe(0);
  });

  it("revalidates the session on the shell's interval through the shell's own revalidator", async () => {
    render(WallLayout, { props: { data: layoutData({}) as never, children } });
    await expect.element(page.getByText("The wall")).toBeVisible();

    expect(mounted.refresh).toEqual([["revalidator", SHELL_REVALIDATE_INTERVAL_MS]]);
  });
});
