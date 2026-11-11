<!--
  A new custom report: choose a starting question (or a blank report), compose it in the workspace
  from signals, a lens and groups with a live preview of each block, then save it under a name. The
  workspace's state is saved with the spec, so the report reopens in the workspace. "Change in the
  form" moves the report to the form builder, which saves the spec alone.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { createCustomReport, getCustomReportCatalogue } from "#lib/api/generated/customReports.generated.remote.js";
  import ReportBuilder from "#lib/components/reports/custom/builder/ReportBuilder.svelte";
  import { minuteLabel } from "#lib/components/reports/custom/workspace/clock.js";
  import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
  import ReportWorkspace from "#lib/components/reports/custom/workspace/ReportWorkspace.svelte";
  import { specToSave } from "#lib/components/reports/custom/workspace/saved-report.js";
  import SaveControl from "#lib/components/reports/custom/workspace/SaveControl.svelte";
  import StarterList from "#lib/components/reports/custom/workspace/StarterList.svelte";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import Save from "@lucide/svelte/icons/save";

  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });

  /** Where the member is: choosing a start, in the workspace, or in the form with a spec. */
  let mode = $state<
    { at: "start" } | { at: "workspace"; report: Report } | { at: "form"; spec: Record<string, unknown> }
  >({ at: "start" });
  /** The name typed in the form; null until the member types, so the report's title is offered. */
  let typed = $state<string | null>(null);
  let saving = $state(false);
  let saveError = $state<string | null>(null);

  function opened(id: string | undefined) {
    return goto(resolve("/(authenticated)/reports/custom/[id]", { id: id ?? "" }));
  }

  async function saveWorkspace(report: Report, name: string) {
    const saved = await createCustomReport({ name, spec: specToSave(report) });
    await opened(saved.id);
  }

  async function saveForm(spec: Record<string, unknown>, name: string) {
    if (name === "") return;
    saving = true;
    saveError = null;
    try {
      const saved = await createCustomReport({ name, spec });
      await opened(saved.id);
    } catch (err) {
      saveError = describeSubmitError(err, "Could not save the report. Try again.");
    } finally {
      saving = false;
    }
  }

  function titleOf(spec: Record<string, unknown>, maxName: number): string {
    return typeof spec.title === "string" ? spec.title.slice(0, maxName) : "";
  }
</script>

<div class="@container space-y-6">
  {#if page.data.limitTo24Hours === true}
    <p class="text-sm text-muted-foreground" role="status" data-testid="clamped-note">
      Your access is limited to the last 24 hours. It cannot create reports.
    </p>
  {:else if catalogue.current}
    {@const current = catalogue.current}
    {#if mode.at === "start"}
      <StarterList onstart={(starter) => (mode = { at: "workspace", report: new Report(current, starter, minuteLabel) })} />
    {:else if mode.at === "workspace"}
      {@const report = mode.report}
      {#key report}
        <ReportWorkspace
          {report}
          onrestart={() => (mode = { at: "start" })}
          onform={(spec) => (mode = { at: "form", spec })}
        >
          {#snippet actions()}
            <SaveControl {report} onsave={(name) => saveWorkspace(report, name)} />
          {/snippet}
        </ReportWorkspace>
      {/key}
    {:else}
      {@const maxName = current.maxNameLength}
      <ReportBuilder catalogue={current} spec={mode.spec} disabled={saving}>
        {#snippet actions({ spec, empty })}
          {@const name = typed ?? titleOf(spec(), maxName)}
          <form
            class="flex flex-col gap-2 @lg:flex-row @lg:items-end"
            onsubmit={(event) => {
              event.preventDefault();
              void saveForm(spec(), name.trim());
            }}
          >
            <div class="flex flex-1 flex-col gap-1.5">
              <Label for="custom-report-name">Name</Label>
              <Input
                id="custom-report-name"
                value={name}
                oninput={(event) => (typed = event.currentTarget.value)}
                maxlength={maxName}
                required
                autocomplete="off"
              />
            </div>
            <Button type="submit" disabled={saving || empty || name.trim() === ""}>
              <Save aria-hidden="true" />
              Save
            </Button>
          </form>
          {#if saveError}
            <p class="mt-2 text-sm text-destructive" role="alert">{saveError}</p>
          {/if}
        {/snippet}
      </ReportBuilder>
    {/if}
  {/if}
</div>
