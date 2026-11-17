<script lang="ts">
  import type { Snippet } from "svelte";

  const { children }: { children: Snippet } = $props();
</script>

<div data-testid="sticky-bar" class="sticky top-(--app-sticky-top,0px) z-20 h-10 bg-card">bar</div>
<div class="h-[3000px]"></div>
{@render children()}
