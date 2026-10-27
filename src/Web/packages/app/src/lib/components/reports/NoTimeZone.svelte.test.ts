import { describe, expect, it } from "vitest";
import { createRawSnippet } from "svelte";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { TimeZoneUnavailableReason } from "#lib/api/index.js";
import NoTimeZone from "./NoTimeZone.svelte";

const pageSentence = createRawSnippet(() => ({ render: () => "<span>This page sentence stays.</span>" }));

describe("NoTimeZone", () => {
  it.each([
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/, undefined],
    [TimeZoneUnavailableReason.LookupFailed, /The time zone of this data did not load/, /reload the report later/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognize the time zone/, /correct the time zone/],
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/, /set a time zone/],
  ])("replaces the page with the page sentence and the cause for %s", async (reason, cause, remedy) => {
    render(NoTimeZone, { props: { reason, children: pageSentence } });

    await expect.element(page.getByText("This report needs a time zone")).toBeVisible();
    await expect.element(page.getByText("This page sentence stays.")).toBeVisible();
    await expect.element(page.getByText(cause)).toBeVisible();
    if (remedy) await expect.element(page.getByText(remedy)).toBeVisible();
    else await expect.element(page.getByText(/The owner of this data|To try again/)).not.toBeInTheDocument();
  });
});
