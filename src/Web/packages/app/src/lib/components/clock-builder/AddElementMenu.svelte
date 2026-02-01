<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Popover from "$lib/components/ui/popover";
  import { Plus } from "lucide-svelte";
  import {
    ELEMENT_GROUPS,
    ELEMENT_INFO,
    type ClockElementType,
  } from "$lib/clock-builder";

  interface Props {
    position: "top" | "bottom";
    open: boolean;
    onOpenChange: (open: boolean) => void;
    onAddElement: (type: ClockElementType, position: "top" | "bottom") => void;
  }

  let { position, open, onOpenChange, onAddElement }: Props = $props();
</script>

<Popover.Root {open} {onOpenChange}>
  <Popover.Trigger class="w-full">
    <div
      class="flex h-8 w-full items-center justify-center rounded-lg border border-dashed border-white/20 text-white/30 transition-colors hover:border-white/40 hover:text-white/50"
    >
      <Plus class="size-4" />
    </div>
  </Popover.Trigger>
  <Popover.Content
    class="w-64"
    side={position === "top" ? "bottom" : "top"}
    align="center"
  >
    <div class="space-y-3">
      <h4 class="font-medium">Add Element</h4>
      {#each ELEMENT_GROUPS as group}
        <div>
          <p class="mb-1 text-xs font-medium text-muted-foreground">
            {group.name}
          </p>
          <div class="flex flex-wrap gap-1">
            {#each group.types as type}
              <Button
                variant="outline"
                size="sm"
                class="h-7 text-xs"
                onclick={() => onAddElement(type, position)}
              >
                {ELEMENT_INFO[type].name}
              </Button>
            {/each}
          </div>
        </div>
      {/each}
    </div>
  </Popover.Content>
</Popover.Root>
