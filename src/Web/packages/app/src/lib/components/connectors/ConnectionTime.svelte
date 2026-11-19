<script lang="ts">
  import { formatDateTime } from "#lib/utils/formatting.js";
  import { formatConnectionTime, isJustNow } from "./connection-time.js";

  let {
    label,
    at,
    fallback,
    now,
  }: {
    /** Words in front of the time, in the muted grade. */
    label?: string;
    at: string | undefined;
    /** Shown in place of the time when there is none. Without it, nothing renders. */
    fallback?: string;
    /** Reference time. Pass a ticking value where the text must age on screen. */
    now?: number;
  } = $props();

  const text = $derived(formatConnectionTime(at, now));
</script>

{#if text && at}
  <span class="whitespace-nowrap">
    {label}
    <time datetime={at} title={formatDateTime(at)} class="text-foreground">
      {#if isJustNow(at, now)}
        just now
      {:else}
        {text}
      {/if}
    </time>
  </span>
{:else if fallback}
  <span class="whitespace-nowrap">
    {label}
    <span class="text-foreground">{fallback}</span>
  </span>
{/if}
