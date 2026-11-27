<script lang="ts">
  import type { StaffGroup } from "@nocturne/api-client";

  const { group }: { group: StaffGroup } = $props();
</script>

<span>{group.name}</span>
{#if group.sharesName}
  <span class="text-muted-foreground" aria-hidden="true">·</span>
  <span class="text-muted-foreground">{group.teamName}</span>
{/if}
