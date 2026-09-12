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

// Sixteen bullets and a sixteen-character token, matching ShareTokenGenerator.TokenLength;
// the component only echoes what it is handed, so a shorter fixture would still pass while
// documenting a shape the server never sends.
const REDACTED_URL = `https://${"•".repeat(16)}.share.example.com`;
const PLAIN_URL = "https://abcdefghjkmnpqrs.share.example.com";

const enabledShare = {
  enabled: true,
  url: null,
  redactedUrl: REDACTED_URL as string | null,
  canReveal: true,
  fullHistory: false,
  scopes: ["glucose.read"],
  lastAccessedAt: null,
};
const disabledShare = {
  enabled: false,
  url: null,
  redactedUrl: null,
  canReveal: false,
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

let revealResult = { ...enabledShare, url: PLAIN_URL as string | null, canReveal: true };
const revealShareLink = vi.fn(async () => revealResult);

vi.mock("$api/generated/shareLinks.generated.remote", () => ({
  getShareLink: () => registerShareQuery(),
  disableShareLink: () => disableShareLink(),
  revealShareLink: () => revealShareLink(),
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
    revealResult = { ...enabledShare, url: PLAIN_URL, canReveal: true };
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

  it("shows the link redacted until asked, and does not fetch it before then", async () => {
    render(PublicAccessCard);

    await expect
      .element(browser.getByTestId("public-access-url-redacted"))
      .toHaveTextContent(REDACTED_URL);
    // The secret must not ride along on the read that renders the card.
    expect(revealShareLink).not.toHaveBeenCalled();

    await browser.getByTestId("public-access-reveal").click();

    await expect
      .element(browser.getByTestId("public-access-url"))
      .toHaveTextContent(PLAIN_URL);
    expect(revealShareLink).toHaveBeenCalledTimes(1);

    await browser.getByTestId("public-access-reveal").click();

    await expect
      .element(browser.getByTestId("public-access-url-redacted"))
      .toHaveTextContent(REDACTED_URL);
    // Hiding and showing again reuses what this visit already fetched.
    await browser.getByTestId("public-access-reveal").click();
    expect(revealShareLink).toHaveBeenCalledTimes(1);
  });

  it("copies without putting the link on screen", async () => {
    const clipboard: string[] = [];
    // defineProperty, not Object.assign: navigator.clipboard is getter-only in a real browser.
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async (text: string) => {
          clipboard.push(text);
        },
      },
    });

    render(PublicAccessCard);
    await expect
      .element(browser.getByTestId("public-access-url-redacted"))
      .toBeVisible();

    await browser.getByRole("button", { name: "Copy" }).click();

    await vi.waitFor(() => expect(clipboard).toEqual([PLAIN_URL]));
    // The point of fetching separately from showing: the link reaches the clipboard
    // without ever being rendered where someone behind you could read it.
    expect(
      document.querySelector('[data-testid="public-access-url"]')
    ).toBeNull();
    await expect
      .element(browser.getByTestId("public-access-url-redacted"))
      .toBeVisible();
  });

  it("withdraws the controls when the reveal finds the link unreadable", async () => {
    // A changed instance key: the columns still show a ciphertext, so the card renders
    // the controls, and only the attempt can discover the copy no longer decrypts.
    revealResult = { ...enabledShare, url: null, canReveal: false };
    render(PublicAccessCard);

    await browser.getByTestId("public-access-reveal").click();

    await vi.waitFor(() =>
      expect(
        document.querySelector('[data-testid="public-access-reveal"]')
      ).toBeNull()
    );
    expect(document.body.textContent?.replace(/\s+/g, " ")).toContain(
      "Nocturne can no longer show you what it is"
    );
  });

  it("offers no way to see a link the server cannot reproduce", async () => {
    share = { ...enabledShare, canReveal: false };
    render(PublicAccessCard);

    await expect
      .element(browser.getByTestId("public-access-url-redacted"))
      .toHaveTextContent(REDACTED_URL);
    expect(
      document.querySelector('[data-testid="public-access-reveal"]')
    ).toBeNull();
    // Normalised: the copy wraps across source lines, so the rendered text carries
    // the template's own newlines and indentation mid-sentence.
    expect(document.body.textContent?.replace(/\s+/g, " ")).toContain(
      "Nocturne can no longer show you what it is"
    );
  });
});
