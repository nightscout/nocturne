import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { page as pageState } from "$app/state";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const stepDown = vi.hoisted(() => vi.fn());

vi.mock("#lib/api/generated/memberInvites.generated.remote.js", () => ({ stepDown }));
vi.mock("#lib/api/generated/roles.generated.remote.js", () => ({
  getRoles: () => remoteQuery(() => []),
}));

import HubStepDown from "./HubStepDown.svelte";

describe("stepping down from the setup hub", () => {
  beforeEach(() => {
    stepDown.mockReset();
    pageState.data.isHandedOffSession = false;
  });

  it("offers Step down to the person's own sign-in", async () => {
    render(HubStepDown);

    await expect.element(page.getByRole("button", { name: "Step down" })).toBeVisible();
    expect(page.getByTestId("sign-in-gate").elements()).toHaveLength(0);
  });

  it("offers a handed-off session a sign-in in place of Step down", async () => {
    pageState.data.isHandedOffSession = true;
    render(HubStepDown);

    await expect.element(page.getByText("Sign in again to step down.")).toBeVisible();
    expect(page.getByRole("button", { name: "Step down" }).elements()).toHaveLength(0);
    expect(stepDown).not.toHaveBeenCalled();
  });
});
