<script lang="ts">
  import type { Snippet } from "svelte";
  import { TimeZoneUnavailableReason } from "$lib/api";
  import Globe from "@lucide/svelte/icons/globe";
  import { timeZoneReasonCopy } from "./time-zone-reasons.svelte";

  let { reason, children }: { reason: TimeZoneUnavailableReason; children: Snippet } = $props();

  const copy = $derived(timeZoneReasonCopy(reason));
</script>

<div class="flex flex-col items-center gap-3 rounded-lg border border-dashed px-6 py-10 text-center">
  <Globe class="size-10 text-muted-foreground/50" />
  <h2 class="font-medium">This report needs a time zone</h2>
  <p class="max-w-prose text-sm text-muted-foreground">
    {@render children()}
    {copy.cause}
    {#if copy.remedy}
      {copy.remedy}
    {/if}
  </p>
</div>
