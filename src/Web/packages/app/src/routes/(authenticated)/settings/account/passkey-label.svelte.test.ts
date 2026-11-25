import { error } from "@sveltejs/kit";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  registerOptions: vi.fn(),
  completeUpdates: vi.fn(),
  registerComplete: vi.fn(),
  startRegistration: vi.fn(),
}));

vi.mock("@simplewebauthn/browser", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@simplewebauthn/browser")>()),
  startRegistration: state.startRegistration,
}));
vi.mock("$app/state", () => ({
  page: {
    url: new URL("https://acme.nocturne.example/settings/account"),
    state: {},
    data: { tenantless: true },
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  replaceState: vi.fn(),
  invalidateAll: vi.fn(),
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  registerOptions: state.registerOptions,
  registerComplete: state.registerComplete,
  removeCredential: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  getRecoveryStatus: () => remoteQuery(() => ({ hasCodes: true, remainingCodes: 5, totalCodes: 10 })),
  listCredentials: () => remoteQuery(() => ({ primaryAuthFactorCount: 1, credentials: [] })),
}));
vi.mock("#lib/api/generated/totps.generated.remote.js", () => ({
  setup: vi.fn(),
  verifySetup: vi.fn(),
  removeCredential: vi.fn(),
  listCredentials: () => remoteQuery(() => []),
}));
vi.mock("#lib/api/generated/oidcs.generated.remote.js", () => ({
  unlinkIdentity: vi.fn(),
  getLinkedIdentities: () => remoteQuery(() => ({ identities: [] })),
}));
vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
  getProvidersInfo: () => remoteQuery(() => ({ providers: [] })),
}));
vi.mock("#lib/components/settings/ActiveSessions.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/settings/TrustedDevices.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/account/UserProfileCard.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/account/TotpSetupDialog.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));

import AccountPage from "./+page.svelte";

describe("Naming a new passkey", () => {
  beforeEach(() => {
    state.registerOptions.mockReset();
    state.registerComplete.mockReset();
    state.completeUpdates.mockReset();
    state.startRegistration.mockReset();
    state.registerOptions
      .mockResolvedValueOnce({ options: '{"challenge":"first"}', challengeToken: "token-1" })
      .mockResolvedValueOnce({ options: '{"challenge":"second"}', challengeToken: "token-2" });
    state.startRegistration.mockResolvedValue({ id: "credential-1" });
    state.registerComplete.mockReturnValue({ updates: state.completeUpdates });
  });

  async function addPasskey() {
    await page.getByRole("button", { name: "Add passkey" }).click();
    await expect.element(page.getByText("Name your passkey")).toBeInTheDocument();
  }

  it("closes the dialog when the passkey is not added, and a retry asks for new options", async () => {
    state.completeUpdates.mockRejectedValueOnce(new Error("network"));
    render(AccountPage, { data: { user: { name: "alex" } } } as never);

    await addPasskey();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Name your passkey")).not.toBeInTheDocument();
    await expect
      .element(page.getByText("Nocturne did not add the passkey. To try again, select Add passkey."))
      .toBeInTheDocument();
    expect(state.registerComplete).toHaveBeenCalledTimes(1);

    await addPasskey();
    expect(state.registerOptions).toHaveBeenCalledTimes(2);
    expect(state.startRegistration).toHaveBeenLastCalledWith({ optionsJSON: { challenge: "second" } });
  });

  it("shows the next step, not the server's reason, when the server refuses the passkey", async () => {
    state.completeUpdates.mockImplementationOnce(async () => error(400, "Passkey registration failed"));
    render(AccountPage, { data: { user: { name: "alex" } } } as never);

    await addPasskey();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Name your passkey")).not.toBeInTheDocument();
    await expect
      .element(page.getByText("Nocturne did not add the passkey. To try again, select Add passkey."))
      .toBeInTheDocument();
    await expect.element(page.getByText("Passkey registration failed")).not.toBeInTheDocument();
  });

  it("shows the rate-limit text when the server limits the attempts", async () => {
    state.completeUpdates.mockImplementationOnce(async () => error(429, "Too Many Requests"));
    render(AccountPage, { data: { user: { name: "alex" } } } as never);

    await addPasskey();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Too many attempts. Wait a few minutes and try again.")).toBeInTheDocument();
    await expect
      .element(page.getByText("Nocturne did not add the passkey. To try again, select Add passkey."))
      .not.toBeInTheDocument();
  });

  it("closes the dialog when the passkey is added", async () => {
    state.completeUpdates.mockResolvedValueOnce(undefined);
    render(AccountPage, { data: { user: { name: "alex" } } } as never);

    await addPasskey();
    await page.getByRole("button", { name: "Save" }).click();

    await expect.element(page.getByText("Name your passkey")).not.toBeInTheDocument();
    await expect.element(page.getByText("Passkey added successfully.")).toBeInTheDocument();
    expect(state.registerComplete).toHaveBeenCalledWith(
      expect.objectContaining({ challengeToken: "token-1" })
    );
  });
});
