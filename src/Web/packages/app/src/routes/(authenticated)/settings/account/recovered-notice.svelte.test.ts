import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, beforeEach, vi } from "vitest";
import { remoteQuery } from "#lib/test-stubs/remote-resource.js";

const state = vi.hoisted(() => ({
  url: new URL("https://acme.nocturne.example/settings/account"),
}));

const navigation = vi.hoisted(() => ({
  replaceState: vi.fn((_url: string, _state: unknown) => {}),
}));

vi.mock("$app/state", () => ({
  page: {
    get url() {
      return state.url;
    },
    state: {},
    data: { tenantless: true },
  },
}));
vi.mock("$app/navigation", async (importOriginal) => ({
  ...(await importOriginal<typeof import("$app/navigation")>()),
  ...navigation,
}));
vi.mock("#lib/api/generated/passkeys.generated.remote.js", () => ({
  registerOptions: vi.fn(),
  registerComplete: vi.fn(),
  removeCredential: vi.fn(),
  regenerateRecoveryCodes: vi.fn(),
  getRecoveryStatus: () => remoteQuery(() => ({ hasCodes: true, remainingCodes: 5, totalCodes: 10 })),
  listCredentials: () =>
    remoteQuery(() => ({
      primaryAuthFactorCount: 3,
      credentials: [
        { id: "old-passkey", label: "Lost phone" },
        { id: "new-passkey", label: "alex's passkey" },
      ],
    })),
}));
vi.mock("#lib/api/generated/totps.generated.remote.js", () => ({
  setup: vi.fn(),
  verifySetup: vi.fn(),
  removeCredential: vi.fn(),
  listCredentials: () => remoteQuery(() => []),
}));
vi.mock("#lib/api/generated/oidcs.generated.remote.js", () => ({
  unlinkIdentity: vi.fn(),
  getLinkedIdentities: () =>
    remoteQuery(() => ({
      identities: [
        { id: "old-identity", providerId: "p1", providerName: "Old provider" },
        { id: "new-identity", providerId: "p2", providerName: "New provider" },
      ],
    })),
}));
vi.mock("#routes/(unauthenticated)/auth/auth.remote.js", () => ({
  getProvidersInfo: () => remoteQuery(() => ({ providers: [] })),
}));
vi.mock("#lib/components/settings/ActiveSessions.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/settings/TrustedDevices.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/account/UserProfileCard.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));
vi.mock("#lib/components/account/TotpSetupDialog.svelte", () => import("#lib/test-stubs/Empty.test-stub.svelte"));

import AccountPage from "./+page.svelte";

function visit(search: string) {
  state.url = new URL(`https://acme.nocturne.example/settings/account${search}`);
  return render(AccountPage, { data: { user: { name: "alex" } } } as never);
}

describe("Account settings after a recovery", () => {
  beforeEach(() => {
    navigation.replaceState.mockClear();
  });

  it("shows no notice on an ordinary visit", async () => {
    visit("");

    await expect.element(page.getByText("Passkeys", { exact: true })).toBeInTheDocument();
    await expect.element(page.getByTestId("recovered-notice")).not.toBeInTheDocument();
    await expect.element(page.getByText("Added just now")).not.toBeInTheDocument();
  });

  it("shows the notice and marks the passkey just added, and only that one", async () => {
    visit("?recovered=1&credentialId=new-passkey");

    await expect.element(page.getByTestId("recovered-notice")).toBeInTheDocument();
    await expect.element(page.getByText("You are back in your account.")).toBeInTheDocument();
    await expect.element(page.getByText("is marked", { exact: false })).toBeInTheDocument();
    await expect.element(page.getByText("Added just now", { exact: true })).toBeInTheDocument();
    expect(page.getByText("Added just now", { exact: true }).elements()).toHaveLength(1);
    await expect.element(page.getByText("alex's passkey")).toBeInTheDocument();
    const marked = page.getByText("alex's passkey").element().closest("p");
    expect(marked?.textContent).toContain("Added just now");
    const older = page.getByText("Lost phone", { exact: true }).element().closest("p");
    expect(older?.textContent).not.toContain("Added just now");
  });

  it("marks the identity a provider recovery linked", async () => {
    visit("?recovered=1&linkedIdentity=new-identity");

    await expect.element(page.getByTestId("recovered-notice")).toBeInTheDocument();
    await expect.element(page.getByText("Check your sign-in methods.")).toBeInTheDocument();
    await expect.element(page.getByText("is marked", { exact: false })).not.toBeInTheDocument();
    await expect.element(page.getByText("New provider")).toBeInTheDocument();
    const marked = page.getByText("New provider").element().closest("p");
    expect(marked?.textContent).toContain("Added just now");
    const older = page.getByText("Old provider", { exact: true }).element().closest("p");
    expect(older?.textContent).not.toContain("Added just now");
  });

  it("shows the notice with no mark when the id matches nothing", async () => {
    visit("?recovered=1&credentialId=unknown");

    await expect.element(page.getByTestId("recovered-notice")).toBeInTheDocument();
    await expect.element(page.getByText("Lost phone", { exact: true })).toBeInTheDocument();
    await expect.element(page.getByText("Added just now", { exact: true })).not.toBeInTheDocument();
    await expect.element(page.getByText("Check your sign-in methods.")).toBeInTheDocument();
    await expect.element(page.getByText("You are back in your account.")).not.toBeInTheDocument();
    await expect.element(page.getByText("is marked", { exact: false })).not.toBeInTheDocument();
  });

  it("shows only the neutral notice for a bare recovered parameter", async () => {
    visit("?recovered=1");

    await expect.element(page.getByText("Check your sign-in methods.")).toBeInTheDocument();
    await expect.element(page.getByText("You are back in your account.")).not.toBeInTheDocument();
  });

  it("strips the recovery parameters from the address bar", async () => {
    visit("?recovered=1&credentialId=new-passkey");

    await vi.waitFor(() => expect(navigation.replaceState).toHaveBeenCalled());
    expect(navigation.replaceState.mock.calls[0][0]).toBe("/settings/account");
  });

  it("lets the person dismiss the notice, and keeps every method", async () => {
    visit("?recovered=1&credentialId=new-passkey");

    await page.getByRole("button", { name: "Dismiss" }).click();

    await expect.element(page.getByTestId("recovered-notice")).not.toBeInTheDocument();
    await expect.element(page.getByText("Lost phone", { exact: true })).toBeInTheDocument();
    await expect.element(page.getByText("alex's passkey")).toBeInTheDocument();
  });
});
