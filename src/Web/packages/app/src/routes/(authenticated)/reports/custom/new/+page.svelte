<!--
  A new custom report: start from a template the API offers or from no panels, compose it in the
  builder with a live preview, then save it under a name.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import {
    createCustomReport,
    getCustomReportCatalogue,
    getCustomReportTemplates,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import ReportBuilder from "#lib/components/reports/custom/builder/ReportBuilder.svelte";
  import * as Item from "#lib/components/ui/item/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import FilePlus from "@lucide/svelte/icons/file-plus";
  import LayoutTemplate from "@lucide/svelte/icons/layout-template";
  import Save from "@lucide/svelte/icons/save";
  import Undo2 from "@lucide/svelte/icons/undo-2";

  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });
  const templates = contextResource(() => getCustomReportTemplates(), {
    errorTitle: "Could not load the report templates",
  });

  /** The spec the builder opened with; null while the member chooses how to start. */
  let start = $state<{ spec: unknown } | null>(null);
  /** The name typed so far; null until the member types, so the report's title is offered. */
  let typed = $state<string | null>(null);
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  function titleOf(spec: Record<string, unknown>): string {
    return typeof spec.title === "string" ? spec.title.slice(0, catalogue.current?.maxNameLength ?? 120) : "";
  }

  async function save(spec: Record<string, unknown>, name: string) {
    if (name === "") return;
    saving = true;
    saveError = null;
    try {
      const saved = await createCustomReport({ name, spec });
      await goto(resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" }));
    } catch (err) {
      saveError = describeSubmitError(err, "Could not save the report. Try again.");
    } finally {
      saving = false;
    }
  }
</script>

<div class="@container space-y-6">
  {#if catalogue.current && templates.current}
    {#if start === null}
      <div class="space-y-1">
        <h2 class="text-xl font-semibold">Start a report</h2>
        <p class="text-sm text-muted-foreground">Start from a template and change it, or start with no panels.</p>
      </div>
      <div class="grid gap-3 @lg:grid-cols-2 @3xl:grid-cols-3" data-testid="report-starts">
        <Item.Root variant="dashed" size="lg" class="items-start" onclick={() => (start = { spec: { version: 1, panels: [] } })}>
          <Item.Media>
            <FilePlus class="size-4 text-muted-foreground" aria-hidden="true" />
          </Item.Media>
          <Item.Content>
            <Item.Title>Blank</Item.Title>
            <Item.Description>A report with no panels. Add the panels that you want.</Item.Description>
          </Item.Content>
        </Item.Root>
        {#each templates.current as template (template.id)}
          <Item.Root variant="outline" size="lg" class="items-start" onclick={() => (start = { spec: template.spec })}>
            <Item.Media>
              <LayoutTemplate class="size-4 text-muted-foreground" aria-hidden="true" />
            </Item.Media>
            <Item.Content>
              <Item.Title>{template.name}</Item.Title>
              <Item.Description>{template.description}</Item.Description>
            </Item.Content>
          </Item.Root>
        {/each}
      </div>
    {:else}
      <ReportBuilder catalogue={catalogue.current} spec={start.spec} disabled={saving}>
        {#snippet actions({ spec, empty })}
          {@const name = typed ?? titleOf(spec())}
          <form
            class="flex flex-col gap-2 @lg:flex-row @lg:items-end"
            onsubmit={(event) => {
              event.preventDefault();
              void save(spec(), name.trim());
            }}
          >
            <div class="flex flex-1 flex-col gap-1.5">
              <Label for="custom-report-name">Name</Label>
              <Input
                id="custom-report-name"
                value={name}
                oninput={(event) => (typed = event.currentTarget.value)}
                maxlength={catalogue.current?.maxNameLength ?? 120}
                required
                autocomplete="off"
              />
            </div>
            <div class="flex gap-2">
              <Button type="submit" disabled={saving || empty || name.trim() === ""}>
                <Save aria-hidden="true" />
                Save
              </Button>
              <Button type="button" variant="outline" onclick={() => (start = null)} disabled={saving}>
                <Undo2 aria-hidden="true" />
                Start again
              </Button>
            </div>
          </form>
          {#if saveError}
            <p class="mt-2 text-sm text-destructive" role="alert">{saveError}</p>
          {/if}
        {/snippet}
      </ReportBuilder>
    {/if}
  {/if}
</div>
