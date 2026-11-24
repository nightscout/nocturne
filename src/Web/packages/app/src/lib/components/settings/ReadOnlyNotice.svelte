<script lang="ts">
  import type { Snippet } from "svelte";
  import type { HTMLAttributes } from "svelte/elements";
  import { Card, CardContent } from "#lib/components/ui/card/index.js";
  import Lock from "@lucide/svelte/icons/lock";

  let {
    children,
    ...rest
  }: HTMLAttributes<HTMLDivElement> & { children: Snippet } = $props();
</script>

<Card {...rest}>
  <CardContent class="flex items-center gap-3 py-4">
    <Lock class="h-5 w-5 shrink-0 text-muted-foreground" />
    <p class="text-sm text-muted-foreground">
      {@render children()}
    </p>
  </CardContent>
</Card>
