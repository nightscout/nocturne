<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { TabsTrigger } from "@nocturne/ui/ui/tabs";
  import { PaintedUnderline } from "@nocturne/watercolour";

  let { ref = $bindable(null), ...restProps }: Omit<ComponentProps<typeof TabsTrigger>, "indicator"> = $props();
</script>

<TabsTrigger bind:ref {...restProps}>
  {#snippet indicator({ active }: { active: boolean })}
    <PaintedUnderline {active} />
  {/snippet}
</TabsTrigger>
