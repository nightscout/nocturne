import { beforeEach, describe, expect, it, vi } from "vitest";
import { render } from "vitest-browser-svelte";
import { page as browserPage } from "vitest/browser";
import { page } from "$app/state";
import { remoteQuery } from "$lib/test-stubs/remote-resource";

let faces: { id: string; name: string }[] = [];

vi.mock("$api/generated/clockFaces.generated.remote", () => ({
  list: () => remoteQuery(() => faces),
  create: vi.fn(),
  remove: vi.fn(),
}));
vi.mock("$lib/components/clock/ClockFacePreview.svelte", () => ({ default: () => {} }));

import ClockPage from "./+page.svelte";

describe("clock page", () => {
  beforeEach(() => {
    page.data.effectivePermissions = ["glucose.read"];
    faces = [{ id: "face-1", name: "Bedside" }];
  });

  it("offers a viewer the faces and no way to create, edit or delete one", async () => {
    render(ClockPage);

    await expect.element(browserPage.getByText("Bedside")).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Open" })).toBeVisible();
    expect(browserPage.getByRole("button", { name: "New clock" }).query()).toBeNull();
    expect(browserPage.getByRole("button", { name: "Edit" }).query()).toBeNull();
    expect(browserPage.getByRole("button", { name: "Delete clock face" }).query()).toBeNull();
  });

  it("offers a caller holding sharing.manage create, edit and delete", async () => {
    page.data.effectivePermissions = ["sharing.manage"];
    render(ClockPage);

    await expect.element(browserPage.getByRole("button", { name: "New clock" })).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Edit" })).toBeVisible();
    await expect.element(browserPage.getByRole("button", { name: "Delete clock face" })).toBeInTheDocument();
  });

  it("offers the create button on an empty list only to a caller holding sharing.manage", async () => {
    faces = [];
    const viewer = render(ClockPage);
    await expect.element(browserPage.getByText("No clock faces yet")).toBeVisible();
    expect(browserPage.getByRole("button", { name: "Create Clock Face" }).query()).toBeNull();
    viewer.unmount();

    page.data.effectivePermissions = ["sharing.manage"];
    render(ClockPage);
    await expect.element(browserPage.getByRole("button", { name: "Create Clock Face" })).toBeVisible();
  });
});
