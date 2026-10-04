import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";
import { page as appPage } from "$lib/test-stubs/app-state";

const hub = vi.hoisted(() => ({
  status: undefined as unknown,
  asked: 0,
  dismiss: vi.fn(),
}));
vi.mock("$api/generated/setupHubs.generated.remote", () => ({
  getSetupHub: () => {
    hub.asked += 1;
    return remoteQuery(() => hub.status);
  },
  dismissSetupStrip: hub.dismiss,
}));

import SetupStrip from "./SetupStrip.svelte";

const strip = () => page.getByTestId("setup-strip");

function status(showStrip: boolean, revision = "r3") {
  return { items: [], resolvedCount: 3, openCount: 3, totalCount: 6, revision, showStrip };
}

beforeEach(() => {
  appPage.data.effectivePermissions = ["*"];
  hub.status = status(true);
  hub.asked = 0;
  hub.dismiss.mockReset().mockResolvedValue(status(false));
});

describe("SetupStrip", () => {
  it("offers the hub with how far setup has got", async () => {
    render(SetupStrip);

    await expect.element(strip()).toHaveTextContent(/3 of 6 set up/);
    await expect
      .element(strip().getByRole("link", { name: "Continue" }))
      .toHaveAttribute("href", "/setup");
  });

  it("stays away when the server says not to offer it", async () => {
    hub.status = status(false);
    render(SetupStrip);

    await expect.poll(() => hub.asked).toBe(1);
    await expect.element(strip()).not.toBeInTheDocument();
  });

  it("hides at once on dismissal and tells the server the revision it showed", async () => {
    render(SetupStrip);

    await strip().getByRole("button", { name: "Dismiss setup reminder" }).click();

    await expect.element(strip()).not.toBeInTheDocument();
    expect(hub.dismiss).toHaveBeenCalledWith({ revision: "r3" });
  });

  it("comes back when the hub has moved on from the dismissed revision", async () => {
    const view = render(SetupStrip);
    await strip().getByRole("button", { name: "Dismiss setup reminder" }).click();
    await expect.element(strip()).not.toBeInTheDocument();

    hub.status = status(true, "r4");
    view.unmount();
    render(SetupStrip);

    await expect.element(strip()).toBeVisible();
  });

  it("never asks for the hub of a member who is not an owner", async () => {
    appPage.data.effectivePermissions = ["glucose.read"];
    render(SetupStrip);

    await expect.element(strip()).not.toBeInTheDocument();
    expect(hub.asked).toBe(0);
  });
});
