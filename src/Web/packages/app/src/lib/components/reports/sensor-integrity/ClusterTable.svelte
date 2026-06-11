<script lang="ts">
  /** Sortable list of flagged windows with an explainable, factual "why flagged" description. */
  import * as Table from "$lib/components/ui/table";
  import type { GlucoseCluster } from "$lib/api";
  import { bgRange } from "$lib/utils/formatting";
  import {
    confidenceLabel,
    confidenceChipClass,
    describeCluster,
    formatDateTime,
    formatDuration,
  } from "./format";

  interface Props {
    clusters: GlucoseCluster[];
    onSelect?: (cluster: GlucoseCluster) => void;
  }

  let { clusters, onSelect }: Props = $props();

  const sorted = $derived(
    [...clusters].sort((a, b) => (a.start?.getTime() ?? 0) - (b.start?.getTime() ?? 0))
  );
</script>

{#if sorted.length === 0}
  <p class="py-6 text-center text-sm text-muted-foreground">
    No windows were flagged in this period.
  </p>
{:else}
  <Table.Root>
    <Table.Header>
      <Table.Row>
        <Table.Head>When</Table.Head>
        <Table.Head class="text-right">Duration</Table.Head>
        <Table.Head class="text-right">Range</Table.Head>
        <Table.Head>Confidence</Table.Head>
        <Table.Head>Why flagged</Table.Head>
      </Table.Row>
    </Table.Header>
    <Table.Body>
      {#each sorted as cluster (cluster.start?.getTime() ?? Math.random())}
        <Table.Row
          class={onSelect ? "cursor-pointer" : ""}
          onclick={() => onSelect?.(cluster)}
        >
          <Table.Cell class="font-medium">{formatDateTime(cluster.start)}</Table.Cell>
          <Table.Cell class="text-right tabular-nums">
            {formatDuration(cluster.durationMinutes)}
          </Table.Cell>
          <Table.Cell class="text-right tabular-nums">
            {cluster.minMgdl != null && cluster.maxMgdl != null
              ? bgRange(cluster.minMgdl, cluster.maxMgdl)
              : "—"}
          </Table.Cell>
          <Table.Cell>
            <span
              class="rounded-full px-2 py-0.5 text-xs font-medium {confidenceChipClass(
                cluster.confidence
              )}"
            >
              {confidenceLabel(cluster.confidence)}
            </span>
          </Table.Cell>
          <Table.Cell class="text-muted-foreground">{describeCluster(cluster)}</Table.Cell>
        </Table.Row>
      {/each}
    </Table.Body>
  </Table.Root>
{/if}
