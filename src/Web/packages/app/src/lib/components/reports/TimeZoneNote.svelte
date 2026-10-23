<script lang="ts">
  import type { Snippet } from "svelte";
  import { TimeZoneUnavailableReason } from "$lib/api";
  import { timeZoneReasonCopy } from "./time-zone-reasons.svelte";

  let { reason, children }: { reason: TimeZoneUnavailableReason; children: Snippet } = $props();

  const copy = $derived(timeZoneReasonCopy(reason));
</script>

{copy.cause}
{@render children()}
{#if copy.remedy}
  {copy.remedy}
{/if}
