import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";

const { clientInfo, consent } = vi.hoisted(() => ({
  clientInfo: { current: undefined as unknown, error: undefined as unknown },
  consent: vi.fn(),
}));

vi.mock("../oauth.remote", async () => {
  const { remoteForm } = await import("#lib/test-stubs/remote-resource.js");
  return { getClientInfo: () => clientInfo, consentForm: remoteForm((data: unknown) => consent(data)) };
});

import ConsentPage from "./+page.svelte";

const approve = () => browserPage.getByRole("button", { name: "Approve" });

describe("consent approval", () => {
  beforeEach(() => {
    page.data.isAuthenticated = true;
    page.url = new URL(
      "http://localhost/oauth/consent?client_id=trio&redirect_uri=trio%3A%2F%2Fcb&scope=treatments.readwrite&code_challenge=abc"
    ) as typeof page.url;
    clientInfo.current = undefined;
    clientInfo.error = undefined;
  });

  it("keeps Approve disabled until the API says what the app can delete", async () => {
    render(ConsentPage);

    await expect.element(approve()).toBeVisible();
    await expect.element(approve()).toBeDisabled();
    await expect
      .element(browserPage.getByText("This app cannot delete your data."))
      .not.toBeInTheDocument();
  });

  it("enables Approve beside the deletion disclosure once it is known", async () => {
    clientInfo.current = {
      clientId: "trio",
      displayName: "Trio",
      isKnown: true,
      deletionAccess: "ReadWrite",
    };
    render(ConsentPage);

    await expect
      .element(browserPage.getByText("This app can change and delete the data in the categories above."))
      .toBeVisible();
    await expect.element(approve()).toBeEnabled();
  });

  it("shows the error state, with nothing to approve, when the app details do not load", async () => {
    clientInfo.error = new Error("client-info failed");
    render(ConsentPage);

    await expect
      .element(browserPage.getByText("Could not load the details of this app."))
      .toBeVisible();
    await expect.element(approve()).not.toBeInTheDocument();
  });

  it("shows why the server refused, and keeps Approve usable", async () => {
    consent.mockRejectedValueOnce({ status: 400, body: { message: "The redirect address is not allowed" } });
    clientInfo.current = { clientId: "trio", displayName: "Trio", isKnown: true, deletionAccess: "ReadWrite" };
    render(ConsentPage);

    await approve().click();

    await expect.element(browserPage.getByRole("alert")).toHaveTextContent("The redirect address is not allowed");
    await expect.element(approve()).toBeEnabled();
  });
});
