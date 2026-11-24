import { render } from "vitest-browser-svelte";
import { page as browser } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { tick } from "svelte";
import { page } from "$app/state";
import { createQueryResource, remoteQuery, remoteQueryError } from "#lib/test-stubs/remote-resource.js";
import { formatMediumDate } from "#lib/utils/formatting.js";
import { accessEndDate, accessEndInstant } from "./access-end.js";

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

/** The share read as the wire carries it: absent URLs are `null`. */
interface ShareState {
  enabled: boolean;
  url: string | null;
  redactedUrl: string | null;
  canReveal: boolean;
  fullHistory: boolean;
  scopes: string[];
  shareableScopes: string[];
  lastAccessedAt: string | null;
  callerAccessEndsAt: string | null;
}

const SHAREABLE_SCOPES = [
  "alerts.read",
  "devices.read",
  "food.read",
  "glucose.read",
  "heartrate.read",
  "reports.read",
  "stepcount.read",
  "treatments.read",
];

const enabledShare: ShareState = {
  enabled: true,
  url: null,
  redactedUrl: REDACTED_URL,
  canReveal: true,
  fullHistory: false,
  scopes: ["glucose.read"],
  shareableScopes: SHAREABLE_SCOPES,
  lastAccessedAt: null,
  callerAccessEndsAt: null,
};
const disabledShare: ShareState = {
  enabled: false,
  url: null,
  redactedUrl: null,
  canReveal: false,
  fullHistory: false,
  scopes: [],
  shareableScopes: SHAREABLE_SCOPES,
  lastAccessedAt: null,
  callerAccessEndsAt: null,
};

let share = $state.raw<ShareState>(enabledShare);
let readState: "answered" | "loading" | "failed" = "answered";

function registerShareQuery() {
  registered += 1;
  $effect.pre(() => () => {
    registered -= 1;
  });
  if (readState === "loading") return createQueryResource(() => new Promise<ShareState>(() => {}));
  if (readState === "failed") return remoteQueryError<ShareState>(new Error("unreachable"));
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

let revealResult: ShareState = { ...enabledShare, url: PLAIN_URL, canReveal: true };
const revealShareLink = vi.fn(async () => revealResult);

vi.mock("#lib/api/generated/shareLinks.generated.remote.js", () => ({
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
    readState = "answered";
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
      "Nocturne can no longer show you what the link is"
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
      "Nocturne can no longer show you what the link is"
    );
  });
  describe("for a caller whose access ends on a date", () => {
    const lastDay = "2030-06-15";
    const reason = () =>
      `Your access ends on ${formatMediumDate(accessEndDate(accessEndInstant(lastDay)))}, so you cannot change public sharing.`;

    it("disables every control that changes sharing, and says why with the date", async () => {
      share = { ...enabledShare, callerAccessEndsAt: accessEndInstant(lastDay) };
      render(PublicAccessCard);

      const line = browser.getByTestId("public-access-locked");
      await expect.element(line).toHaveTextContent(reason());

      await expect.element(browser.getByTestId("public-access-toggle")).toBeDisabled();
      await expect.element(browser.getByRole("button", { name: "Regenerate" })).toBeDisabled();
      const switches = browser.getByRole("switch").elements();
      expect(switches.length).toBeGreaterThan(SHAREABLE_SCOPES.length);
      for (const control of switches) {
        expect(control.hasAttribute("disabled") || control.getAttribute("aria-disabled") === "true").toBe(true);
        expect(control.getAttribute("aria-describedby")).toBe("public-access-locked");
      }
      const windowItems = browser.getByTestId("public-access-window").getByRole("radio").elements();
      expect(windowItems.length).toBe(2);
      for (const item of windowItems) {
        expect(item.hasAttribute("disabled") || item.getAttribute("data-disabled") !== null).toBe(true);
        expect(item.getAttribute("aria-describedby")).toBe("public-access-locked");
      }
      await expect
        .element(browser.getByRole("button", { name: "Regenerate" }))
        .toHaveAttribute("aria-describedby", "public-access-locked");
    });

    it("still shows the link", async () => {
      share = { ...enabledShare, callerAccessEndsAt: accessEndInstant(lastDay) };
      render(PublicAccessCard);

      await expect.element(browser.getByTestId("public-access-locked")).toBeVisible();
      await browser.getByTestId("public-access-reveal").click();

      await expect.element(browser.getByTestId("public-access-url")).toHaveTextContent(PLAIN_URL);
      expect(revealShareLink).toHaveBeenCalledTimes(1);
    });

    it("disables the switch that turns sharing on when it is off", async () => {
      share = { ...disabledShare, callerAccessEndsAt: accessEndInstant(lastDay) };
      render(PublicAccessCard);

      await expect.element(browser.getByTestId("public-access-locked")).toBeVisible();
      await expect.element(browser.getByTestId("public-access-toggle")).toBeDisabled();
    });
  });

  it("disables nothing and shows no reason for a caller whose access does not end", async () => {
    render(PublicAccessCard);

    await expect.element(browser.getByTestId("public-access-window")).toBeVisible();

    await expect.element(browser.getByTestId("public-access-toggle")).toBeEnabled();
    await expect.element(browser.getByRole("button", { name: "Regenerate" })).toBeEnabled();
    for (const control of browser.getByRole("switch").elements()) {
      expect(control.hasAttribute("disabled")).toBe(false);
    }
    expect(document.querySelector('[data-testid="public-access-locked"]')).toBeNull();
  });
  describe.each([
    ["a caller whose access ends", { ...enabledShare, callerAccessEndsAt: accessEndInstant("2030-06-15") }],
    ["a caller whose access does not end", enabledShare],
  ])("before the read answers, for %s", (_name, answer) => {
    it("keeps the main switch disabled while the read loads, and says nothing is off", async () => {
      share = answer;
      readState = "loading";
      render(PublicAccessCard);

      await expect.element(browser.getByText("Public access", { exact: true })).toBeVisible();
      await expect.element(browser.getByTestId("public-access-toggle")).toBeDisabled();
      expect(document.body.textContent).not.toContain("Public access is off.");
      expect(document.body.textContent).not.toContain("did not load");
    });

    it("states that the read failed, offers to try again, and keeps the switch disabled", async () => {
      share = answer;
      readState = "failed";
      render(PublicAccessCard);

      await expect
        .element(browser.getByText("The public access settings did not load."))
        .toBeVisible();
      await expect.element(browser.getByRole("button", { name: "Try again" })).toBeVisible();
      await expect.element(browser.getByTestId("public-access-toggle")).toBeDisabled();
      expect(document.body.textContent).not.toContain("Public access is off.");
    });
  });
});
