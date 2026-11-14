<script lang="ts">
  /**
   * Each profile version of the range as one row, newest first, with the
   * 24-hour shape of its schedules drawn once. A row opens the change that
   * started it.
   */
  import type { ProfileHistoryEntry, ProfileVersion } from "#lib/api/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { formatMediumDateRange } from "#lib/utils/formatting.js";
  import DayShape from "./DayShape.svelte";
  import { spanLength, versionLabel } from "./timeline-format.js";

  interface Props {
    versions: ProfileVersion[];
    /** The start times that are a change, which is every version's but the first. */
    changeTimes: ReadonlySet<number>;
    selectedChange: number | null;
    onSelectChange?: (mills: number) => void;
  }

  let { versions, changeTimes, selectedChange, onSelectChange }: Props = $props();

  const newestFirst = $derived([...versions].reverse());

  function domainOf(pick: (v: ProfileVersion) => ProfileHistoryEntry[] | undefined): [number, number] {
    const values = versions.flatMap((v) => (pick(v) ?? []).flatMap((e) => [e.value ?? 0, e.high ?? e.value ?? 0]));
    if (values.length === 0) return [0, 1];
    const max = Math.max(...values);
    const min = Math.min(...values);
    return [Math.min(0, min), max === 0 ? 1 : max * 1.1];
  }

  const columns = $derived([
    { label: "Basal", pick: (v: ProfileVersion) => v.basal, domain: domainOf((v) => v.basal), range: false },
    {
      label: "Sensitivity",
      pick: (v: ProfileVersion) => v.sensitivity,
      domain: domainOf((v) => v.sensitivity),
      range: false,
    },
    {
      label: "Carb ratio",
      pick: (v: ProfileVersion) => v.carbRatio,
      domain: domainOf((v) => v.carbRatio),
      range: false,
    },
    { label: "Target", pick: (v: ProfileVersion) => v.targets, domain: domainOf((v) => v.targets), range: true },
  ]);
</script>

<div class="overflow-x-auto">
  <table class="w-full min-w-[640px] text-sm">
    <thead>
      <tr class="text-xs text-muted-foreground">
        <th scope="col" class="py-2 pr-3 text-left font-medium">Version</th>
        {#each columns as column (column.label)}
          <th scope="col" class="w-[17%] px-2 py-2 text-left font-medium">
            {column.label}
            <span class="font-normal">00–24 h</span>
          </th>
        {/each}
      </tr>
    </thead>
    <tbody>
      {#each newestFirst as version (version.startMills)}
        {@const start = version.startMills ?? 0}
        {@const isChange = changeTimes.has(start)}
        <tr
          class="border-t border-border align-middle {selectedChange === start ? 'bg-accent/50' : ''}"
        >
          <th scope="row" class="py-2 pr-3 text-left font-normal">
            {#if isChange}
              <Button
                variant="link"
                size="inline"
                aria-pressed={selectedChange === start}
                onclick={() => onSelectChange?.(start)}
              >
                {versionLabel(version)}
              </Button>
            {:else}
              <span class="font-medium">{versionLabel(version)}</span>
            {/if}
            <span class="block text-xs text-muted-foreground tabular-nums">
              {formatMediumDateRange(new Date(start), new Date(version.endMills ?? 0))}
              · {spanLength(start, version.endMills ?? 0)}
            </span>
          </th>
          {#each columns as column (column.label)}
            <td class="px-2 py-2">
              <div class="h-10">
                <DayShape entries={column.pick(version) ?? []} yDomain={column.domain} range={column.range} />
              </div>
            </td>
          {/each}
        </tr>
      {/each}
    </tbody>
  </table>
</div>
