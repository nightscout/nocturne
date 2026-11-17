import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect } from "vitest";
import PermissionSummary from "./PermissionSummary.svelte";

describe("PermissionSummary", () => {
  it("shows read and write levels under the shared category labels", async () => {
    render(PermissionSummary, {
      props: { permissions: ["glucose.read", "treatments.readwrite"] },
    });

    await expect.element(page.getByText("Blood glucose", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Treatments", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Read & write", { exact: true })).toBeVisible();
  });

  it("lists managing audit settings without the audit log row it implies", async () => {
    render(PermissionSummary, { props: { permissions: ["audit.manage", "audit.read"] } });

    await expect.element(page.getByText("Manage audit settings", { exact: true })).toBeVisible();
    await expect.element(page.getByText("View audit logs", { exact: true })).not.toBeInTheDocument();
  });

  it("collapses a full-access grant to one row", async () => {
    render(PermissionSummary, { props: { permissions: ["*", "glucose.read"] } });

    await expect.element(page.getByText("All permissions", { exact: true })).toBeVisible();
    await expect.element(page.getByText("Blood glucose", { exact: true })).not.toBeInTheDocument();
  });
});
