<script lang="ts">
  /**
   * Table of hypoglycemia events that followed a flagged window, with the insulin dosed during the
   * window. Factual log — it links a detection to a subsequent low, it does not assert causation.
   */
  import * as Table from "$lib/components/ui/table";
  import Moon from "lucide-svelte/icons/moon";
  import type { SensorIntegrityHypoEvent } from "$lib/api";
  import { bg, bgLabel, formatDateTimeCompact } from "$lib/utils/formatting";
  import { confidenceLabel, confidenceChipClass } from "./format";

  interface Props {
    events: SensorIntegrityHypoEvent[];
  }

  let { events }: Props = $props();

  // DTO date fields are ISO strings at runtime despite their Date type; wrap before use.
  const startMs = (e: SensorIntegrityHypoEvent) =>
    e.event?.cluster?.start ? new Date(e.event.cluster.start).getTime() : 0;

  const sorted = $derived([...events].sort((a, b) => startMs(a) - startMs(b)));

  function insulinTotal(e: SensorIntegrityHypoEvent): number {
    return (e.event?.insulinDuringCluster ?? []).reduce((sum, d) => sum + (d.units ?? 0), 0);
  }
</script>

{#if sorted.length === 0}
  <p class="py-6 text-center text-sm text-muted-foreground">
    No hypoglycemia followed a flagged window in this period.
  </p>
{:else}
  <Table.Root>
    <Table.Header>
      <Table.Row>
        <Table.Head>Window</Table.Head>
        <Table.Head>Confidence</Table.Head>
        <Table.Head class="text-right">Nadir</Table.Head>
        <Table.Head class="text-right">Time to nadir</Table.Head>
        <Table.Head class="text-right">Insulin in window</Table.Head>
        <Table.Head class="text-center">Overnight</Table.Head>
      </Table.Row>
    </Table.Header>
    <Table.Body>
      {#each sorted as e, i (i)}
        {@const cluster = e.event?.cluster}
        {@const units = insulinTotal(e)}
        <Table.Row>
          <Table.Cell class="font-medium">{formatDateTimeCompact(cluster?.start)}</Table.Cell>
          <Table.Cell>
            <span
              class="rounded-full px-2 py-0.5 text-xs font-medium {confidenceChipClass(
                cluster?.confidence
              )}"
            >
              {confidenceLabel(cluster?.confidence)}
            </span>
          </Table.Cell>
          <Table.Cell class="text-right tabular-nums">
            {e.event?.nadirMgdl != null ? `${bg(e.event.nadirMgdl)} ${bgLabel()}` : "—"}
          </Table.Cell>
          <Table.Cell class="text-right tabular-nums">
            {e.event?.timeToNadirHours != null ? `${e.event.timeToNadirHours.toFixed(1)} h` : "—"}
          </Table.Cell>
          <Table.Cell class="text-right tabular-nums">
            {units > 0 ? `${units.toFixed(2)} U` : "—"}
          </Table.Cell>
          <Table.Cell class="text-center">
            {#if e.isNocturnal}
              <Moon class="mx-auto h-4 w-4 text-cluster-high" />
            {:else}
              <span class="text-muted-foreground">—</span>
            {/if}
          </Table.Cell>
        </Table.Row>
      {/each}
    </Table.Body>
  </Table.Root>
{/if}
