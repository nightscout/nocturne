<script lang="ts">
  import type { EntryRecord } from "$lib/constants/entry-categories";
  import {
    ENTRY_CATEGORIES,
    getEntryStyle,
  } from "$lib/constants/entry-categories";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import { Link } from "lucide-svelte";
  import {
    formatDateTimeCompact,
    formatInsulinDisplay,
    formatCarbDisplay,
    bg,
    bgLabel,
  } from "$lib/utils/formatting";

  interface Props {
    records: EntryRecord[];
    activeRecordId: string;
    onSwitch: (record: EntryRecord) => void;
  }

  let { records, activeRecordId, onSwitch }: Props = $props();

  function getPrimaryValue(r: EntryRecord): string {
    switch (r.kind) {
      case "bolus":
        return r.data.insulin != null
          ? `${formatInsulinDisplay(r.data.insulin)}U`
          : "\u2014";
      case "carbs":
        return r.data.carbs != null
          ? `${formatCarbDisplay(r.data.carbs)}g`
          : "\u2014";
      case "bgCheck":
        return r.data.mgdl != null ? `${bg(r.data.mgdl)} ${bgLabel()}` : "\u2014";
      case "note":
        return r.data.text || "\u2014";
      case "deviceEvent":
        return r.data.eventType ?? "\u2014";
      case "basalInjection":
        return r.data.units != null
          ? `${formatInsulinDisplay(r.data.units)}U`
          : "\u2014";
    }
  }

  function formatMills(mills: number | undefined): string {
    if (!mills) return "\u2014";
    return formatDateTimeCompact(new Date(mills).toISOString());
  }
</script>

{#if records.length > 1}
  <Separator />
  <div class="space-y-3">
    <h4
      class="text-xs font-medium uppercase text-muted-foreground tracking-wide flex items-center gap-2"
    >
      <Link class="h-3.5 w-3.5" />
      Linked Records
      <Badge variant="secondary" class="text-xs h-5 px-1.5">
        {records.length}
      </Badge>
    </h4>
    {#each records as linked}
      {@const linkedStyle = getEntryStyle(linked.kind)}
      {@const linkedCategory = ENTRY_CATEGORIES[linked.kind]}
      {@const isActive = linked.data.id === activeRecordId}
      <button
        type="button"
        class="w-full text-left rounded-lg border p-3 transition-colors {isActive
          ? 'border-primary bg-primary/5'
          : 'hover:bg-muted/50'}"
        disabled={isActive}
        onclick={() => onSwitch(linked)}
      >
        <div class="flex items-center gap-2">
          <Badge
            variant="outline"
            class="{linkedStyle.colorClass} {linkedStyle.bgClass} {linkedStyle.borderClass} text-xs"
          >
            {linkedCategory.name}
          </Badge>
          <span class="text-sm">{getPrimaryValue(linked)}</span>
          <span class="ml-auto text-xs text-muted-foreground">
            {formatMills(linked.data.mills)}
          </span>
        </div>
      </button>
    {/each}
  </div>
{/if}
