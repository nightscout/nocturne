<script lang="ts">
  import {
    TenantOverviewAttentionKind,
    type TenantOverviewItem,
  } from "#lib/api/generated/nocturne-api-client.js";
  import {
    VALUE_TONE,
    alertLevel,
    alertLevelPrefix,
  } from "#lib/components/status-pills/tracker-pill-style.svelte.js";
  import History from "@lucide/svelte/icons/history";
  import Info from "@lucide/svelte/icons/info";
  import TriangleAlert from "@lucide/svelte/icons/triangle-alert";
  import { attentionPhrase } from "./attention-phrases.svelte.js";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const reasons = $derived(
    (person.attention ?? [])
      .map((reason) => ({ reason, phrase: attentionPhrase(reason, person) }))
      .filter((entry) => entry.phrase !== "")
  );
</script>

{#if reasons.length > 0}
  <ul class="flex flex-wrap gap-x-3 gap-y-0.5" data-testid="attention-reasons">
    {#each reasons as { reason, phrase }, index (index)}
      {@const level = alertLevel(reason.level ?? null)}
      {@const prefix = alertLevelPrefix(level)}
      {@const noReading =
        reason.kind === TenantOverviewAttentionKind.NoRecentReading}
      <li
        class="flex items-center gap-1 text-xs font-medium {noReading
          ? 'text-muted-foreground'
          : VALUE_TONE[level]}"
      >
        {#if noReading}
          <History class="size-3.5" aria-hidden="true" />
        {:else if prefix}
          <TriangleAlert class="size-3.5" aria-hidden="true" />
        {:else}
          <Info class="size-3.5" aria-hidden="true" />
        {/if}
        {#if prefix}
          <span class="sr-only">{prefix}</span>
        {/if}
        {phrase}
      </li>
    {/each}
  </ul>
{/if}
