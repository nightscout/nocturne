import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import CopyInvitationMessageButton from "./CopyInvitationMessageButton.svelte";

const INVITE_URL = "https://sam.nocturne.example/join?token=abc123";

let clipboard: string[];

beforeEach(() => {
  clipboard = [];
  Object.defineProperty(navigator, "clipboard", {
    configurable: true,
    value: {
      writeText: async (text: string) => {
        clipboard.push(text);
      },
    },
  });
});

describe("CopyInvitationMessageButton", () => {
  it("copies a message naming the inviter and carrying the link", async () => {
    render(CopyInvitationMessageButton, {
      props: {
        url: INVITE_URL,
        inviterName: "Alex Carer",
        onCopied: () => {},
        onCopyFailed: () => {},
      },
    });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(clipboard).toHaveLength(1));
    expect(clipboard[0]).toContain(INVITE_URL);
    expect(clipboard[0]).toMatch(/^Alex Carer has invited you to Nocturne/);
    await expect
      .element(page.getByRole("button", { name: "Message copied" }))
      .toBeVisible();
  });

  it("still carries the link when the inviter has no name", async () => {
    // The API sends an empty name, not a missing one, for a subject without a display name.
    render(CopyInvitationMessageButton, {
      props: {
        url: INVITE_URL,
        inviterName: "",
        onCopied: () => {},
        onCopyFailed: () => {},
      },
    });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(clipboard).toHaveLength(1));
    expect(clipboard[0]).toContain(INVITE_URL);
    expect(clipboard[0]).toMatch(/^You've been invited to Nocturne/);
  });

  it("reports a successful copy so a stale error can clear", async () => {
    const onCopied = vi.fn();
    const onCopyFailed = vi.fn();
    render(CopyInvitationMessageButton, {
      props: { url: INVITE_URL, onCopied, onCopyFailed },
    });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(onCopied).toHaveBeenCalledOnce());
    expect(onCopyFailed).not.toHaveBeenCalled();
  });

  it("reports a clipboard refusal", async () => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async () => {
          throw new Error("denied");
        },
      },
    });
    const onCopied = vi.fn();
    const onCopyFailed = vi.fn();
    render(CopyInvitationMessageButton, {
      props: { url: INVITE_URL, onCopied, onCopyFailed },
    });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(onCopyFailed).toHaveBeenCalledOnce());
    expect(onCopied).not.toHaveBeenCalled();
  });
});
