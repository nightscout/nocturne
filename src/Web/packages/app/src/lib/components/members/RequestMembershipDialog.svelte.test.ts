import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { beforeEach, describe, expect, it, vi } from "vitest";

const state = vi.hoisted(() => ({
  supported: true,
  accessRequestOptions: vi.fn(),
  accessRequestComplete: vi.fn(),
  startRegistration: vi.fn(),
}));

vi.mock("@simplewebauthn/browser", () => ({
  browserSupportsWebAuthn: () => state.supported,
  startRegistration: state.startRegistration,
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  accessRequestOptions: state.accessRequestOptions,
  accessRequestComplete: state.accessRequestComplete,
}));

import RequestMembershipDialog from "./RequestMembershipDialog.svelte";

beforeEach(() => {
  state.supported = true;
  state.accessRequestOptions.mockReset();
  state.accessRequestComplete.mockReset();
  state.startRegistration.mockReset();
  state.accessRequestOptions.mockResolvedValue({
    options: '{"challenge":"abc"}',
    challengeToken: "token-1",
  });
  state.startRegistration.mockResolvedValue({ id: "credential-1" });
  state.accessRequestComplete.mockResolvedValue({ success: true });
});

describe("RequestMembershipDialog", () => {
  it("sends the name and message through the passkey ceremony, then says the owner must approve", async () => {
    render(RequestMembershipDialog, { open: true });
    await expect.element(page.getByLabelText("Your name")).toHaveFocus();

    await page.getByLabelText("Your name").fill("  Sam Smith ");
    await page.getByLabelText("Message to the owner").fill("I'm Sam's endocrinologist");
    await page.getByRole("button", { name: "Send request" }).click();

    await expect.element(page.getByText("Request sent")).toBeInTheDocument();
    await expect.element(page.getByText(/The owner must approve your request/)).toBeInTheDocument();
    expect(state.accessRequestOptions).toHaveBeenCalledWith({
      displayName: "Sam Smith",
      message: "I'm Sam's endocrinologist",
    });
    expect(state.startRegistration).toHaveBeenCalledWith({ optionsJSON: { challenge: "abc" } });
    expect(state.accessRequestComplete).toHaveBeenCalledWith({
      displayName: "Sam Smith",
      attestationResponseJson: JSON.stringify({ id: "credential-1" }),
      challengeToken: "token-1",
    });
  });

  it("cannot send without a name", async () => {
    render(RequestMembershipDialog, { open: true });
    await expect.element(page.getByLabelText("Your name")).toHaveFocus();
    const name = page.getByLabelText("Your name");
    const send = page.getByRole("button", { name: "Send request" });

    await page.getByLabelText("Message to the owner").fill("hello");
    await name.fill("Sam Smith");
    await expect.element(send).toBeEnabled();

    await name.fill("   ");
    await expect.element(send).toBeDisabled();

    await name.fill("");
    await expect.element(send).toBeDisabled();
  });

  it("shows the server's reason and stays on the form when the request is refused", async () => {
    state.accessRequestOptions.mockRejectedValue(
      Object.assign(new Error("A pending access request with this name already exists"), {
        status: 409,
        body: { message: "A pending access request with this name already exists" },
      }),
    );
    render(RequestMembershipDialog, { open: true });
    await expect.element(page.getByLabelText("Your name")).toHaveFocus();

    await page.getByLabelText("Your name").fill("Sam Smith");
    await page.getByRole("button", { name: "Send request" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent(/pending access request with this name/);
    expect(state.startRegistration).not.toHaveBeenCalled();
    expect(state.accessRequestComplete).not.toHaveBeenCalled();
    await expect.element(page.getByText("Request sent")).not.toBeInTheDocument();
  });

  it("files nothing when the visitor cancels the passkey prompt", async () => {
    state.startRegistration.mockRejectedValue(Object.assign(new Error("cancelled"), { name: "NotAllowedError" }));
    render(RequestMembershipDialog, { open: true });
    await expect.element(page.getByLabelText("Your name")).toHaveFocus();

    await page.getByLabelText("Your name").fill("Sam Smith");
    await page.getByRole("button", { name: "Send request" }).click();

    await expect.element(page.getByRole("alert")).toHaveTextContent(/No passkey was created/);
    expect(state.accessRequestComplete).not.toHaveBeenCalled();
  });

  it("says up front that this browser cannot send a request when it has no passkey support", async () => {
    state.supported = false;
    render(RequestMembershipDialog, { open: true });

    await expect.element(page.getByText(/This browser cannot make a passkey/)).toBeInTheDocument();
    await expect.element(page.getByLabelText("Message to the owner")).not.toBeInTheDocument();
    await expect.element(page.getByRole("button", { name: "Send request" })).not.toBeInTheDocument();
  });
});
