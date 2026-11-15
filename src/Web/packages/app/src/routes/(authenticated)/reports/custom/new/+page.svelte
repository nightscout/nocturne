<!--
  A new custom report: choose a starting question, a ready-made report or a blank report, compose it
  in the workspace with a live preview of each block, then save it under a name. The workspace's
  state is saved with the spec, so the report reopens as it was left.
-->
<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import {
    createCustomReport,
    getCustomReportCatalogue,
    getCustomReportTemplates,
  } from "#lib/api/generated/customReports.generated.remote.js";
  import { minuteLabel, spanLabel } from "#lib/components/reports/custom/workspace/clock.js";
  import { Report } from "#lib/components/reports/custom/workspace/report.svelte.js";
  import ReportWorkspace from "#lib/components/reports/custom/workspace/ReportWorkspace.svelte";
  import { specToSave } from "#lib/components/reports/custom/workspace/saved-report.js";
  import SaveControl from "#lib/components/reports/custom/workspace/SaveControl.svelte";
  import StarterList from "#lib/components/reports/custom/workspace/StarterList.svelte";
  import { contextResource } from "#lib/hooks/resource-context.svelte.js";
  import { tick } from "svelte";

  const catalogue = contextResource(() => getCustomReportCatalogue(), {
    errorTitle: "Could not load what a report can show",
  });

  const templates = contextResource(() => getCustomReportTemplates(), {
    errorTitle: "Could not load the ready-made reports",
  });

  /** The report being made, or null while the member chooses a start. */
  let report = $state<Report | null>(null);

  /** Opens the workspace on `made` at the top of the page: a start chosen low in the list leaves the page scrolled. */
  async function begin(made: Report | null) {
    report = made;
    await tick();
    for (const scroller of [document.querySelector("main"), document.scrollingElement]) scroller?.scrollTo({ top: 0 });
  }

  async function save(made: Report, name: string) {
    const saved = await createCustomReport({ name, spec: specToSave(made) });
    await goto(resolve("/(authenticated)/reports/custom/[id]", { id: saved.id ?? "" }));
  }
</script>

<div class="@container space-y-6">
  {#if page.data.limitTo24Hours === true}
    <p class="text-sm text-muted-foreground" role="status" data-testid="clamped-note">
      Your access is limited to the last 24 hours. It cannot create reports.
    </p>
  {:else if catalogue.current}
    {@const current = catalogue.current}
    {#if report === null}
      <StarterList
        onstart={(starter) => begin(new Report(current, starter, minuteLabel, spanLabel))}
        templates={templates.current ?? []}
        ontemplate={(template) => begin(Report.fromSpec(current, template.spec, minuteLabel, spanLabel))}
      />
    {:else}
      {@const made = report}
      {#key made}
        <ReportWorkspace report={made} onrestart={() => (report = null)}>
          {#snippet actions(problem: string | null)}
            <SaveControl report={made} reason={problem} onsave={(name) => save(made, name)} />
          {/snippet}
        </ReportWorkspace>
      {/key}
    {/if}
  {/if}
</div>
