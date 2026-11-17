<script lang="ts">
  import type { ComponentProps } from "svelte";
  import * as Tabs from "./index";

  let { labels, variant }: { labels: string[]; variant?: ComponentProps<typeof Tabs.List>["variant"] } = $props();
</script>

<Tabs.Root value={labels[0]}>
  <Tabs.List {variant}>
    {#each labels as label (label)}
      <Tabs.Trigger value={label}>{label}</Tabs.Trigger>
    {/each}
  </Tabs.List>
</Tabs.Root>
