<script lang="ts">
  import { Badge } from "$lib/components/ui/badge";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";

  const hubQuery = getSetupHub();
  const open = $derived(hubQuery.current?.openCount ?? 0);
</script>

{#if open > 0}
  <Badge variant="secondary" size="sm" class="ml-auto" aria-label="{open} left to set up">
    {open}
  </Badge>
{/if}
