import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi, beforeEach } from "vitest";
import { toast } from "svelte-sonner";
import CopyInvitationMessageButton from "./CopyInvitationMessageButton.svelte";

vi.mock("svelte-sonner", () => ({ toast: { error: vi.fn() } }));

const INVITE_URL = "https://sam.nocturne.example/join?token=abc123";

let clipboard: string[];

beforeEach(() => {
  vi.mocked(toast.error).mockClear();
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
      },
    });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(clipboard).toHaveLength(1));
    expect(clipboard[0]).toContain(INVITE_URL);
    expect(clipboard[0]).toMatch(/^You are invited to Nocturne/);
  });

  it("does not toast on a successful copy", async () => {
    render(CopyInvitationMessageButton, { props: { url: INVITE_URL } });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await expect
      .element(page.getByRole("button", { name: "Message copied" }))
      .toBeVisible();
    expect(toast.error).not.toHaveBeenCalled();
  });

  it("toasts a clipboard refusal and does not show the copied state", async () => {
    Object.defineProperty(navigator, "clipboard", {
      configurable: true,
      value: {
        writeText: async () => {
          throw new Error("denied");
        },
      },
    });
    render(CopyInvitationMessageButton, { props: { url: INVITE_URL } });

    await page.getByRole("button", { name: "Copy invitation message" }).click();

    await vi.waitFor(() => expect(toast.error).toHaveBeenCalledOnce());
    await expect
      .element(page.getByRole("button", { name: "Copy invitation message" }))
      .toBeVisible();
  });
});
