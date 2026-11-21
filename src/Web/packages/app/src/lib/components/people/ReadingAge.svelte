<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import { relativeAge, toDate } from "#lib/utils/formatting.js";
  import History from "@lucide/svelte/icons/history";
  import { readingIsNotCurrent } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const lastReadingAt = $derived(toDate(person.lastReadingAt));
  const notCurrent = $derived(readingIsNotCurrent(person));
</script>

<span
  class="inline-flex items-center gap-1 whitespace-nowrap text-sm tabular-nums {notCurrent
    ? 'font-medium text-foreground'
    : 'text-muted-foreground'}"
  data-testid="reading-age"
>
  {#if notCurrent}
    <History class="size-3.5" aria-hidden="true" data-testid="age-history-icon" />
  {/if}
  {#if lastReadingAt}
    {relativeAge(lastReadingAt)}
  {:else}
    No reading
  {/if}
</span>
