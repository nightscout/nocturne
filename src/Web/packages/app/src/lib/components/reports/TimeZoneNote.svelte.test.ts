import { describe, expect, it } from "vitest";
import { createRawSnippet } from "svelte";
import { render } from "vitest-browser-svelte";
import { page } from "vitest/browser";
import { TimeZoneUnavailableReason } from "$lib/api";
import TimeZoneNote from "./TimeZoneNote.svelte";

const pageSentence = createRawSnippet(() => ({ render: () => "<span>This page sentence stays.</span>" }));

describe("TimeZoneNote", () => {
  it.each([
    [TimeZoneUnavailableReason.Share, /A shared link does not show the time zone/, undefined],
    [TimeZoneUnavailableReason.LookupFailed, /The time zone of this data did not load/, /reload the report later/],
    [TimeZoneUnavailableReason.Unrecognised, /does not recognise the time zone/, /correct the time zone/],
    [TimeZoneUnavailableReason.NotConfigured, /do not have a time zone/, /set a time zone/],
  ])("states why there is no zone for %s, then the page sentence", async (reason, cause, remedy) => {
    const { container } = render(TimeZoneNote, { props: { reason, children: pageSentence } });

    await expect.element(page.getByText(cause)).toBeVisible();
    await expect.element(page.getByText("This page sentence stays.")).toBeVisible();
    expect(container.textContent).toMatch(new RegExp(String.raw`${cause.source}[\s\S]*This page sentence stays\.`));
    if (remedy) await expect.element(page.getByText(remedy)).toBeVisible();
    else await expect.element(page.getByText(/The owner of this data|To try again/)).not.toBeInTheDocument();
  });
});
