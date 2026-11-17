import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";

const { approveDevice } = vi.hoisted(() => ({ approveDevice: vi.fn() }));

vi.mock("../oauth.remote", async () => {
  const { remoteForm, remoteFormFields } = await import("#lib/test-stubs/remote-resource.js");
  const form = (result: unknown) => ({ result, pending: 0, enhance: () => ({}), fields: remoteFormFields() });
  return {
    lookupDeviceForm: form({
      deviceInfo: {
        userCode: "BCDF-GHJK",
        clientId: "0192f0e4-0000-7000-8000-000000000001",
        clientDisplayName: "Trio",
        scopes: ["glucose.read", "treatments.readwrite"],
        deletionAccess: "ReadWrite",
      },
    }),
    approveDeviceForm: remoteForm((data: unknown) => approveDevice(data)),
    denyDeviceForm: form(undefined),
  };
});

import DevicePage from "./+page.svelte";

describe("device approval", () => {
  beforeEach(() => {
    page.data.isAuthenticated = true;
  });

  it("names the app only as the request's own claim, beside the warning", async () => {
    render(DevicePage);

    const notice = browserPage.getByRole("note");
    await expect.element(notice).toHaveTextContent("This request says it is from Trio");
    await expect.element(notice).toHaveTextContent("cannot prove which app started it");
    await expect
      .element(browserPage.getByText("A device wants to access your Nocturne data."))
      .toBeVisible();
    expect(browserPage.getByText("Trio", { exact: true }).elements()).toHaveLength(1);
    expect(notice.element().contains(browserPage.getByText("Trio", { exact: true }).element())).toBe(true);
  });

  it("discloses the deletion the API reports for read-write scopes", async () => {
    render(DevicePage);

    await expect
      .element(
        browserPage.getByText(
          "This app can change and delete the data in the categories above."
        )
      )
      .toBeVisible();
    await expect
      .element(browserPage.getByText("This app cannot delete your data."))
      .not.toBeInTheDocument();
  });

  it("shows why the server refused the approval, and keeps Approve usable", async () => {
    approveDevice.mockRejectedValueOnce({ status: 400, body: { message: "The code has expired" } });
    render(DevicePage);

    await browserPage.getByRole("button", { name: "Approve" }).click();

    await expect.element(browserPage.getByRole("alert")).toHaveTextContent("The code has expired");
    await expect.element(browserPage.getByRole("button", { name: "Approve" })).toBeEnabled();
  });
});
