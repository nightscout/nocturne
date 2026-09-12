<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { ChevronRight } from "lucide-svelte";
  import type { SettingsLink } from "./settings-links";

  let { link }: { link: SettingsLink } = $props();
</script>

<a href={link.href} class="group block">
  <Card.Root
    class="h-full transition-colors hover:border-primary/40 hover:bg-muted/40"
  >
    <Card.Content class="flex items-center gap-4 p-4">
      <div
        class="flex h-10 w-10 shrink-0 items-center justify-center rounded-lg bg-primary/10"
      >
        <link.icon class="h-5 w-5 text-primary" />
      </div>
      <div class="min-w-0 flex-1">
        <p class="font-medium">{link.title}</p>
        <p class="text-sm text-muted-foreground">{link.description}</p>
      </div>
      <ChevronRight
        class="h-4 w-4 shrink-0 text-muted-foreground transition-transform group-hover:translate-x-0.5"
      />
    </Card.Content>
  </Card.Root>
</a>
