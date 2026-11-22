<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { relativeAge, toDate } from "#lib/utils/formatting.js";
  import History from "@lucide/svelte/icons/history";
  import { getReadState, isShownAsCurrent } from "./read-state.js";

  interface Props {
    person: TenantOverviewItem;
    size?: "sm" | "lg" | "xl" | "2xl";
  }

  const { person, size = "sm" }: Props = $props();

  const read = getReadState();
  const lastReadingAt = $derived(toDate(person.lastReadingAt));
  const notCurrent = $derived(!isShownAsCurrent(person, read));
  const textClass = $derived(
    { sm: "text-sm", lg: "text-base", xl: "text-xl", "2xl": "text-3xl" }[size]
  );
  const iconClass = $derived(
    { sm: "size-3.5", lg: "size-4", xl: "size-5", "2xl": "size-7" }[size]
  );
</script>

<span
  class="inline-flex items-center gap-1 whitespace-nowrap tabular-nums {textClass} {notCurrent
    ? 'font-medium text-foreground'
    : 'text-muted-foreground'}"
  data-testid="reading-age"
>
  {#if notCurrent}
    <History class={iconClass} aria-hidden="true" data-testid="age-history-icon" />
  {/if}
  {#if lastReadingAt}
    {relativeAge(lastReadingAt, { now: read.now })}
  {:else}
    No reading
  {/if}
</span>
