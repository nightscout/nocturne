import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { describe, it, expect, vi } from "vitest";
import { page as pageState } from "$app/state";
import { missingScopeError } from "#lib/forms/submit-messages.svelte.js";

const scopeRefusal = {
  status: 403,
  body: {
    message: "This operation requires the 'translations.contribute' scope.",
    code: "missing_scope",
  },
};

const remote = vi.hoisted(() => ({ clearDrafts: vi.fn() }));

vi.mock("#lib/api/generated/translations.generated.remote.js", () => ({
  getCatalogSource: () => ({ current: undefined, refresh: vi.fn() }),
  getDrafts: () => ({
    current: [{ msgId: "Save", translations: ["Enregistrer"] }],
    refresh: vi.fn(),
  }),
  upsertDrafts: vi.fn(),
  submitContribution: vi.fn(),
  clearDrafts: remote.clearDrafts,
}));

import TranslationsPage from "./+page.svelte";

describe("the translations page", () => {
  it("shows the app's sentence when clearing drafts is refused for a missing scope", async () => {
    Object.assign(pageState, { params: { locale: "fr" } });
    remote.clearDrafts.mockRejectedValue(scopeRefusal);

    const { container } = render(TranslationsPage, {});
    await page.getByRole("button", { name: "Clear drafts" }).click();
    await page.getByRole("button", { name: "Clear drafts" }).last().click();

    await expect.element(page.getByText(missingScopeError())).toBeVisible();
    expect(container.textContent).not.toContain("translations.contribute");
  });
});
