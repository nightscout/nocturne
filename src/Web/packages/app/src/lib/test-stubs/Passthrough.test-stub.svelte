<script lang="ts">
  import type { Snippet } from "svelte";

  // Renders its children and nothing else, for a wrapper whose own behaviour is not under test.
  let { children }: { children?: Snippet; [key: string]: unknown } = $props();
</script>

{@render children?.()}
