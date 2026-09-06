import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { tick } from "svelte";
import { page } from "$app/state";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

// The framework registers a query instance from an `$effect.pre` created wherever the query was
// constructed, and a command's single-flight refresh reaches only the instances still registered.
// Modelling that here is what makes the card's `$derived`-held query observable: a query the
// component has stopped consuming is released, and the refresh below then has nothing to write to.
let registered = 0;

const enabledShare = {
  enabled: true,
  url: null,
  fullHistory: false,
  scopes: ["glucose.read"],
  lastAccessedAt: null,
};
const disabledShare = {
  enabled: false,
  url: null,
  fullHistory: false,
  scopes: [] as string[],
  lastAccessedAt: null,
};

let share = $state.raw<typeof enabledShare>(enabledShare);

function registerShareQuery() {
  registered += 1;
  $effect.pre(() => () => {
    registered -= 1;
  });
  return remoteQuery(() => share);
}

let disableCall: Promise<unknown> | null = null;

const disableShareLink = vi.fn(() => {
  disableCall = (async () => {
    // A round trip, so the optimistic override has flushed before the refresh comes back.
    await tick();
    if (registered > 0) share = disabledShare;
    return disabledShare;
  })();
  return disableCall;
});

vi.mock("$api/generated/shareLinks.generated.remote", () => ({
  getShareLink: () => registerShareQuery(),
  disableShareLink: () => disableShareLink(),
  rotateShareLink: vi.fn(),
  setShareLinkFullHistory: vi.fn(),
  setShareLinkScopes: vi.fn(),
}));

import PublicAccessCard from "./PublicAccessCard.svelte";

describe("PublicAccessCard", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    registered = 0;
    disableCall = null;
    share = enabledShare;
    page.data = { effectivePermissions: ["*"] };
  });

  it("settles on the off state after turning public access off", async () => {
    render(PublicAccessCard);

    await expect
      .element(browser.getByTestId("public-access-window"))
      .toBeVisible();

    await browser.getByTestId("public-access-toggle").click();

    await vi.waitFor(() => expect(disableCall).not.toBeNull());
    await disableCall;
    await tick();
    await tick();

    expect(
      document.querySelector('[data-testid="public-access-window"]')
    ).toBeNull();
    expect(document.body.textContent).toContain("Public access is off.");
  });
});
