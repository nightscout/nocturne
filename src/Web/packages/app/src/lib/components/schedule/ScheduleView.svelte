<script lang="ts">
  import type { Icon } from "lucide-svelte";
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import * as Table from "$lib/components/ui/table";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Plus, Trash2 } from "lucide-svelte";
  import { glucoseUnits } from "$lib/stores/appearance-store.svelte";

  const MGDL_TO_MMOL = 18.01559;

  interface ScheduleEntry {
    time?: string;
    value?: number;
    low?: number;
    high?: number;
  }

  interface Props {
    title: string;
    description?: string;
    icon?: typeof Icon;
    iconClass?: string;
    unit: string;
    entries: ScheduleEntry[];
    /** Source units for glucose-aware conversion. Omit for non-glucose schedules. */
    sourceUnits?: string;
    /** When provided, enables edit mode. Called with updated entries on every change. */
    onchange?: (entries: ScheduleEntry[]) => void;
    /** Numeric input constraints (edit mode only) */
    step?: number;
    min?: number;
  }

  let {
    title,
    description,
    icon,
    iconClass = "text-primary",
    unit,
    entries,
    sourceUnits,
    onchange,
    step = 0.1,
    min = 0,
  }: Props = $props();

  /** Whether this is a range schedule (has low/high) vs single-value */
  let isRange = $derived(entries.some((e) => e.low !== undefined || e.high !== undefined));

  /** Whether editing is enabled */
  let editable = $derived(!!onchange);

  /** Determine if unit conversion is needed (read-only mode only) */
  let needsConversion = $derived.by(() => {
    if (editable || !sourceUnits) return false;
    const src = normalizeUnit(sourceUnits);
    const dst = normalizeUnit(glucoseUnits.current);
    return src !== dst;
  });

  function normalizeUnit(u: string): "mgdl" | "mmol" {
    const lower = u.toLowerCase().replace(/[^a-z]/g, "");
    if (lower.includes("mmol")) return "mmol";
    return "mgdl";
  }

  function convertValue(value: number | undefined): number | undefined {
    if (value === undefined || value === null) return undefined;

    if (!sourceUnits) return value;

    const src = normalizeUnit(sourceUnits);
    const dst = normalizeUnit(glucoseUnits.current);

    if (src === dst) {
      return dst === "mmol"
        ? Math.round(value * 10) / 10
        : Math.round(value);
    }

    if (src === "mgdl" && dst === "mmol") {
      return Math.round((value / MGDL_TO_MMOL) * 10) / 10;
    }

    if (src === "mmol" && dst === "mgdl") {
      return Math.round(value * MGDL_TO_MMOL);
    }

    return value;
  }

  /** Display value: convert in read-only mode, raw in edit mode */
  function displayValue(value: number | undefined): string {
    if (value === undefined || value === null) return "\u2013";
    if (editable) return String(value);
    if (needsConversion) {
      const converted = convertValue(value);
      return converted !== undefined ? String(converted) : "\u2013";
    }
    return String(value);
  }

  // --- Edit mode helpers ---

  function cloneEntries(): ScheduleEntry[] {
    return JSON.parse(JSON.stringify(entries));
  }

  function addEntry() {
    if (!onchange) return;
    const updated = cloneEntries();
    if (isRange) {
      updated.push({ time: "12:00", low: 0, high: 0 });
    } else {
      updated.push({ time: "12:00", value: 0 });
    }
    onchange(updated);
  }

  function removeEntry(index: number) {
    if (!onchange) return;
    const updated = cloneEntries();
    if (updated.length <= 1) return; // minimum one entry
    updated.splice(index, 1);
    onchange(updated);
  }

  function updateEntryTime(index: number, time: string) {
    if (!onchange) return;
    const updated = cloneEntries();
    if (updated[index]) {
      updated[index].time = time;
      onchange(updated);
    }
  }

  function updateEntryValue(index: number, value: number) {
    if (!onchange) return;
    const updated = cloneEntries();
    if (updated[index]) {
      updated[index].value = value;
      onchange(updated);
    }
  }

  function updateEntryLow(index: number, value: number) {
    if (!onchange) return;
    const updated = cloneEntries();
    if (updated[index]) {
      updated[index].low = value;
      onchange(updated);
    }
  }

  function updateEntryHigh(index: number, value: number) {
    if (!onchange) return;
    const updated = cloneEntries();
    if (updated[index]) {
      updated[index].high = value;
      onchange(updated);
    }
  }
</script>

{#if editable}
  <!-- Edit mode: compact input rows, no Card wrapper -->
  <div class="space-y-4">
    <div class="flex items-center justify-between">
      <div class="flex items-center gap-3">
        {#if icon}
          {@const Icon = icon}
          <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
            <Icon class="h-5 w-5 {iconClass}" />
          </div>
        {/if}
        <div>
          <h3 class="font-medium">{title}</h3>
          {#if description}
            <p class="text-sm text-muted-foreground">{description}</p>
          {/if}
        </div>
      </div>
      <Button variant="outline" size="sm" onclick={addEntry}>
        <Plus class="h-4 w-4 mr-1" />
        Add Time Block
      </Button>
    </div>

    <Table.Root>
      <Table.Header>
        <Table.Row>
          <Table.Head>Start Time</Table.Head>
          {#if isRange}
            <Table.Head class="text-right">Low ({unit})</Table.Head>
            <Table.Head class="text-right">High ({unit})</Table.Head>
          {:else}
            <Table.Head class="text-right">{unit}</Table.Head>
          {/if}
          <Table.Head class="w-10"></Table.Head>
        </Table.Row>
      </Table.Header>
      <Table.Body>
        {#each entries as entry, i}
          <Table.Row>
            <Table.Cell>
              <Input
                type="time"
                value={entry.time ?? "00:00"}
                class="w-28"
                onchange={(e: Event & { currentTarget: HTMLInputElement }) =>
                  updateEntryTime(i, e.currentTarget.value)}
              />
            </Table.Cell>
            {#if isRange}
              <Table.Cell class="text-right">
                <Input
                  type="number"
                  {step}
                  {min}
                  value={entry.low ?? 0}
                  class="ml-auto w-24 text-right"
                  onchange={(e: Event & { currentTarget: HTMLInputElement }) =>
                    updateEntryLow(i, Number(e.currentTarget.value))}
                />
              </Table.Cell>
              <Table.Cell class="text-right">
                <Input
                  type="number"
                  {step}
                  {min}
                  value={entry.high ?? 0}
                  class="ml-auto w-24 text-right"
                  onchange={(e: Event & { currentTarget: HTMLInputElement }) =>
                    updateEntryHigh(i, Number(e.currentTarget.value))}
                />
              </Table.Cell>
            {:else}
              <Table.Cell class="text-right">
                <Input
                  type="number"
                  {step}
                  {min}
                  value={entry.value ?? 0}
                  class="ml-auto w-24 text-right"
                  onchange={(e: Event & { currentTarget: HTMLInputElement }) =>
                    updateEntryValue(i, Number(e.currentTarget.value))}
                />
              </Table.Cell>
            {/if}
            <Table.Cell>
              <Button
                variant="ghost"
                size="icon"
                class="h-8 w-8 text-destructive hover:text-destructive"
                disabled={entries.length <= 1}
                onclick={() => removeEntry(i)}
              >
                <Trash2 class="h-4 w-4" />
              </Button>
            </Table.Cell>
          </Table.Row>
        {:else}
          <Table.Row>
            <Table.Cell colspan={isRange ? 4 : 3} class="text-center py-4 text-muted-foreground">
              No time blocks configured. Click "Add Time Block" to get started.
            </Table.Cell>
          </Table.Row>
        {/each}
      </Table.Body>
    </Table.Root>
  </div>
{:else}
  <!-- Read-only mode: Card with icon, title, description, and table -->
  <Card>
    <CardHeader class="pb-3">
      <div class="flex items-center gap-3">
        {#if icon}
          {@const Icon = icon}
          <div class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10">
            <Icon class="h-5 w-5 {iconClass}" />
          </div>
        {/if}
        <div>
          <CardTitle class="text-base">{title}</CardTitle>
          {#if description}
            <CardDescription class="text-xs">{description}</CardDescription>
          {/if}
        </div>
      </div>
    </CardHeader>
    <CardContent>
      <Table.Root>
        <Table.Header>
          <Table.Row>
            <Table.Head>Time</Table.Head>
            {#if isRange}
              <Table.Head class="text-right">Low ({unit})</Table.Head>
              <Table.Head class="text-right">High ({unit})</Table.Head>
            {:else}
              <Table.Head class="text-right">{unit}</Table.Head>
            {/if}
          </Table.Row>
        </Table.Header>
        <Table.Body>
          {#each entries as entry}
            <Table.Row>
              <Table.Cell class="font-mono text-sm">
                {entry.time ?? "\u2013"}
              </Table.Cell>
              {#if isRange}
                <Table.Cell class="text-right font-mono">
                  {displayValue(entry.low)}
                </Table.Cell>
                <Table.Cell class="text-right font-mono">
                  {displayValue(entry.high)}
                </Table.Cell>
              {:else}
                <Table.Cell class="text-right font-mono">
                  {displayValue(entry.value)}
                </Table.Cell>
              {/if}
            </Table.Row>
          {:else}
            <Table.Row>
              <Table.Cell colspan={isRange ? 3 : 2} class="text-center py-4 text-muted-foreground">
                No schedule entries configured.
              </Table.Cell>
            </Table.Row>
          {/each}
        </Table.Body>
      </Table.Root>
    </CardContent>
  </Card>
{/if}
