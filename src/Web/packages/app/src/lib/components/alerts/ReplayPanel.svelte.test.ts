import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

const replayFailure: { error: unknown } = vi.hoisted(() => ({ error: null }));

vi.mock("$api/generated/alertReplays.generated.remote", () => ({
  replay: () => Promise.reject(replayFailure.error),
  replayDryRun: () => Promise.reject(replayFailure.error),
}));
vi.mock("$api/generated/alertRules.generated.remote", () => ({
  getRules: () => remoteQuery(() => []),
}));

import ReplayPanel from "./ReplayPanel.svelte";

describe("ReplayPanel", () => {
  beforeEach(() => {
    replayFailure.error = new Error("network down");
  });

  it("asks the user to try again when the replay fails for no stated reason", async () => {
    render(ReplayPanel, {});

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("Failed to run replay. Try again.");
  });

  it("shows the reason the server gave for refusing the replay", async () => {
    replayFailure.error = { status: 400, body: { message: "The window is longer than 7 days." } };

    render(ReplayPanel, {});

    await expect
      .element(page.getByRole("alert"))
      .toHaveTextContent("The window is longer than 7 days.");
  });
});
