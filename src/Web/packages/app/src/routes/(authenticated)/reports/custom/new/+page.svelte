<!--
  A report an assistant made, opened from its link: `?spec=` carries the spec as the API encoded it.
  The API decodes, validates and runs it for the viewer; the page draws the result and offers Save.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { createCustomReport } from "#lib/api/generated/customReports.generated.remote.js";
  import type { CustomReportRunResponse } from "#lib/api/generated/nocturne-api-client.js";
  import CustomReportQuery from "#lib/components/reports/custom/CustomReportQuery.svelte";
  import CustomReportRun from "#lib/components/reports/custom/CustomReportRun.svelte";
  import { MAX_ENCODED_SPEC_LENGTH } from "#lib/components/reports/custom/custom-report-labels.svelte.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import Save from "@lucide/svelte/icons/save";

  const encoded = $derived(page.url.searchParams.get("spec") ?? "");
  const linkProblem = $derived(
    encoded === ""
      ? "This link has no report in it. Open the report link from the assistant again."
      : encoded.length > MAX_ENCODED_SPEC_LENGTH
        ? "This link is too long. A report link can hold a spec of at most 16 kB."
        : null,
  );

  /** The name typed so far; null until the reader types, so the report's title is offered. */
  let typed = $state<string | null>(null);
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  function titleOf(run: CustomReportRunResponse): string {
    const spec: unknown = run.spec;
    const title = spec !== null && typeof spec === "object" && "title" in spec ? spec.title : undefined;
    return typeof title === "string" ? title.slice(0, 120) : "";
  }

  async function save(run: CustomReportRunResponse, name: string) {
    if (!run.spec || name === "") return;
    saving = true;
    saveError = null;
    try {
      const saved = await createCustomReport({ name, spec: run.spec });
      await goto(resolve(`reports/custom/${saved.id}`));
    } catch (err) {
      saveError = describeSubmitError(err, "Could not save the report. Try again.");
    } finally {
      saving = false;
    }
  }
</script>

<div class="@container space-y-6">
  {#if linkProblem}
    <p class="rounded-lg border border-destructive/40 bg-destructive/5 p-4 text-sm" role="alert">{linkProblem}</p>
  {:else}
    <CustomReportQuery request={{ encoded }}>
      {#snippet children(run)}
        {#if (run.errors ?? []).length === 0 && run.spec != null}
          {@const name = typed ?? titleOf(run)}
          <form
            class="flex flex-col gap-2 rounded-lg border border-border p-4 @lg:flex-row @lg:items-end"
            onsubmit={(event) => {
              event.preventDefault();
              void save(run, name.trim());
            }}
          >
            <div class="flex flex-1 flex-col gap-1.5">
              <Label for="custom-report-name">Name</Label>
              <Input
                id="custom-report-name"
                value={name}
                oninput={(event) => (typed = event.currentTarget.value)}
                maxlength={120}
                required
                autocomplete="off"
              />
            </div>
            <Button type="submit" disabled={saving || name.trim() === ""}>
              <Save class="size-4" aria-hidden="true" />
              Save
            </Button>
          </form>
          {#if saveError}
            <p class="text-sm text-destructive" role="alert">{saveError}</p>
          {/if}
        {/if}
        <CustomReportRun {run} />
      {/snippet}
    </CustomReportQuery>
  {/if}
</div>
