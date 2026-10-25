<script lang="ts">
  import { getHelpReference } from "$api/generated/metadatas.generated.remote";
  import type { HelpArticle } from "$lib/help/documentation-types";
  import { glucoseBands } from "$lib/help/reference.svelte";
  import { bg, getUnitLabel } from "$lib/utils/formatting";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";
  import TextureSwatch from "$lib/components/charts/print/TextureSwatch.svelte";
  import TIRStackedChart from "$lib/components/reports/TIRStackedChart.svelte";
  import DataSourceRow from "$lib/components/settings/DataSourceRow.svelte";
  import { googleHealthDataTypeLabel } from "$lib/components/connectors/google-health-labels.svelte";
  import { Button } from "$lib/components/ui/button";
  import * as Table from "$lib/components/ui/table";
  let { kind }: { kind: NonNullable<HelpArticle["reference"]> } = $props();
  const resource = getHelpReference();
  const facts = $derived(resource.current);
  const bands = $derived(
    facts?.reportBands ? glucoseBands(facts.reportBands) : []
  );
  const unit = $derived(getUnitLabel(glucoseUnits.current));
  const destinations = $derived<Record<string, string>>({
    "step-counts": "Steps report",
    "heart-rates": "Heart Rate report",
    "body-weights": "Weight history",
    "sleep-sessions": "Sleep report",
  });
</script>

<section
  class="space-y-4 rounded-xl border p-5"
  aria-label="Reference from the running application"
>
  <h2 class="font-semibold">Reference from this version</h2>
  <p class="text-sm text-muted-foreground">
    These values come from the same server models and capability catalog used
    by this running Nocturne version.
  </p>
  {#if facts}
    {#if kind === "glucose" && facts.reportBands}
      <Table.Root>
        <Table.Caption>
          Glucose report bands in {unit}. The inequality signs show which
          boundary belongs to each band.
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head>Band</Table.Head><Table.Head>Reading</Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each bands as band (band.key)}
            <Table.Row>
              <Table.Cell>
                <span class="inline-flex items-center gap-2">
                  <TextureSwatch texture={band.key} />{band.title}
                </span>
              </Table.Cell><Table.Cell class="font-mono whitespace-nowrap">
                {#if band.lower !== undefined}{bg(band.lower)}
                  {band.lowerInclusive ? "≤" : "<"}{/if} x {#if band.upper !== undefined}{band.upperInclusive
                    ? "≤"
                    : "<"}
                  {bg(band.upper)}{/if}
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
      <p class="text-sm text-muted-foreground">
        Tight range is a subset of in range, so it is not an extra band to add
        to the total. Personal targets from Therapy and notification thresholds
        from Alert Rules are separate. Display rounding can make two nearby
        boundaries look alike in mmol/L. Classification uses the stored glucose
        value.
      </p>
      {#if facts.maximumPlausibleGlucose !== undefined}<p
          class="text-sm text-muted-foreground"
        >
          Glucose statistics exclude non-positive readings and values at or
          above {bg(facts.maximumPlausibleGlucose)}
          {unit}. Missing or excluded readings are not treated as normal
          glucose. Reading percentages count distinct measurement times. Duration measures use the reading cadence, so gaps matter.
        </p>{/if}
      <figure class="space-y-3 rounded-lg bg-muted/30 p-4">
        <figcaption class="text-sm font-medium">
          Example: invented readings, using the real report chart
        </figcaption>
        <TIRStackedChart
          percentages={{
            veryLow: 1,
            low: 3,
            target: 76,
            tightTarget: 50,
            high: 16,
            veryHigh: 4,
          }}
          thresholds={{
            low: facts.reportBands.targetBottom!,
            high: facts.reportBands.targetTop!,
          }}
          orientation="horizontal"
          keyOnScreen
        />
        <p class="text-sm text-muted-foreground">
          In this example, 76% is in range and 50% is in tight range. The 50% is
          already included in the 76%. Colors, textures, and chart styling use
          the same component as the reports.
        </p>
      </figure>
    {:else if kind === "google-health"}
      <dl class="grid gap-3 text-sm sm:grid-cols-2">
        <div>
          <dt class="font-medium">Callback path</dt>
          <dd class="break-all font-mono">{facts.googleHealthCallbackPath}</dd>
        </div>
        <div>
          <dt class="font-medium">Default automatic sync interval</dt>
          <dd>{facts.googleHealthSyncIntervalMinutes} minutes</dd>
        </div>
        <div>
          <dt class="font-medium">
            Default history when no start date is chosen
          </dt>
          <dd>{facts.googleHealthDefaultHistoryDays} days</dd>
        </div>
        <div>
          <dt class="font-medium">History-days configuration bounds</dt>
          <dd>
            {facts.googleHealthMinimumHistoryDays}–{facts.googleHealthMaximumHistoryDays}
            days. An explicit Import data from date uses a separate date setting.
          </dd>
        </div>
      </dl>
      <h3 class="text-sm font-semibold">
        Read scopes requested for supported imports
      </h3>
      <ul class="space-y-2 text-xs font-mono break-all">
        {#each facts.googleHealthReadScopes ?? [] as scope (scope)}<li>
            {scope}
          </li>{/each}
      </ul>
      <p class="text-sm text-muted-foreground">
        Use these exact scopes in Google Cloud's Data Access configuration.
        Nocturne adds basic sign-in identification during connection. Do not
        substitute an unrelated nutrition scope for a required permission.
      </p>
      <Table.Root>
        <Table.Caption>
          Importable types from this version's connector catalog. Other
          inventory types can be visible without an import destination.
        </Table.Caption>
        <Table.Header>
          <Table.Row>
            <Table.Head>Data type</Table.Head><Table.Head>
              Used in Nocturne
            </Table.Head>
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each facts.googleHealthCapabilities?.filter((c) => c.supported) ?? [] as capability (capability.dataType)}<Table.Row
            >
              <Table.Cell>
                {googleHealthDataTypeLabel(capability.dataType)}
              </Table.Cell><Table.Cell>
                {destinations[capability.destination ?? ""] ??
                  capability.destination}
              </Table.Cell>
            </Table.Row>{/each}
        </Table.Body>
      </Table.Root>
      <figure class="space-y-3 rounded-lg bg-muted/30 p-4">
        <figcaption class="text-sm font-medium">
          Example connection states: no personal data
        </figcaption>
        <DataSourceRow
          name="Google Health"
          icon={undefined}
          status="configured"
          statusMessage="Connected. Choose and save the import selection"
        />
        <DataSourceRow
          name="Google Health"
          icon={undefined}
          status="syncing"
          statusMessage="Import is running. Check historical progress"
        />
        <p class="text-sm text-muted-foreground">
          These use the same status-row component as Connectors & Apps. A
          connection can be configured before any data is imported.
        </p>
      </figure>
    {/if}
  {:else if resource.error}
    <p class="text-sm text-muted-foreground" role="status">
      The current reference values could not be loaded. The written guide is
      still available. Retry to see this server's exact values.
    </p>
    <Button variant="outline" onclick={() => resource.refresh()}>
      Retry reference values
    </Button>
  {:else}
    <p class="text-sm text-muted-foreground" role="status">
      Loading current reference values…
    </p>
  {/if}
</section>
