<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import AttentionReasons from "./AttentionReasons.svelte";
  import PersonAlerts from "./PersonAlerts.svelte";
  import PersonReading from "./PersonReading.svelte";
  import PersonSelect from "./PersonSelect.svelte";
  import PersonStatusLine from "./PersonStatusLine.svelte";
  import PersonTrace from "./PersonTrace.svelte";
  import { personName } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
  }

  const { person }: Props = $props();

  const name = $derived(personName(person));
  const hasTrace = $derived((person.recentReadings?.length ?? 0) > 0);
</script>

<li>
  <PersonSelect {person} class="block space-y-3 px-3 py-4">
    <div class="flex items-start justify-between gap-2">
      <p class="min-w-0 truncate text-base font-semibold" title={name}>
        {name}
      </p>
      <PersonAlerts {person} />
    </div>
    <PersonReading {person} />
    <AttentionReasons {person} />
    {#if hasTrace}
      <div>
        <PersonTrace {person} class="h-16 w-full" />
        <div class="flex justify-between text-xs text-muted-foreground">
          <span>3 h ago</span>
          <span>Now</span>
        </div>
      </div>
    {/if}
    <PersonStatusLine {person} />
  </PersonSelect>
</li>
