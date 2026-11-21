<script lang="ts">
  import type { TenantOverviewItem } from "@nocturne/api-client";
  import AttentionReasons from "./AttentionReasons.svelte";
  import { personName } from "./person.js";

  interface Props {
    person: TenantOverviewItem;
    size?: "sm" | "lg";
    class?: string;
  }

  const { person, size = "sm", class: className = "" }: Props = $props();

  const name = $derived(personName(person));
</script>

<div class="min-w-0 space-y-0.5 {className}">
  <p
    class="truncate {size === 'lg'
      ? 'text-base font-semibold'
      : 'text-sm font-medium'}"
    title={name}
  >
    {name}
  </p>
  <AttentionReasons {person} />
</div>
