<script lang="ts">
  import {
    TenantPersonEntryKind,
    type TenantPersonEntry,
  } from "#lib/api/generated/nocturne-api-client.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import {
    formatCarbDisplay,
    formatDateTime,
    formatInsulinDisplay,
    relativeAge,
  } from "#lib/utils/formatting.js";
  import Lock from "@lucide/svelte/icons/lock";

  interface Props {
    /** Null when the caller may not read this person's treatments. */
    entries: TenantPersonEntry[] | null | undefined;
  }

  const { entries }: Props = $props();

  // A kind this build does not know is not shown as one it does.
  const known = $derived(
    (entries ?? []).filter(
      (entry) =>
        entry.kind === TenantPersonEntryKind.Bolus ||
        entry.kind === TenantPersonEntryKind.Carbs
    )
  );
</script>

{#if entries == null}
  <p class="flex items-center gap-2 text-sm text-muted-foreground">
    <Lock class="size-4 shrink-0" aria-hidden="true" />
    You do not have access to this person's treatments.
  </p>
{:else if known.length === 0}
  <p class="text-sm text-muted-foreground">
    No boluses or carbs in the last 24 hours.
  </p>
{:else}
  <ul class="divide-y divide-border" data-testid="person-entries">
    {#each known as entry, index (index)}
      <li class="flex items-center gap-3 py-2">
        {#if entry.kind === TenantPersonEntryKind.Bolus}
          <Badge variant="entry-bolus" size="fixed">Bolus</Badge>
          {#if entry.units != null}
            <span class="text-sm font-medium tabular-nums">
              {formatInsulinDisplay(entry.units)} U
            </span>
          {/if}
        {:else}
          <Badge variant="entry-carbs" size="fixed">Carbs</Badge>
          {#if entry.grams != null}
            <span class="text-sm font-medium tabular-nums">
              {formatCarbDisplay(entry.grams)} g
            </span>
          {/if}
        {/if}
        <span
          class="ml-auto text-xs text-muted-foreground tabular-nums"
          title={formatDateTime(entry.timestamp)}
        >
          {relativeAge(entry.timestamp)}
        </span>
      </li>
    {/each}
  </ul>
{/if}
