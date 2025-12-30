<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Check, ChevronsUpDown } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { tick } from "svelte";
  import { FOOD_UNITS } from "./food-constants";

  interface Props {
    /** Currently selected unit */
    value: string;
    /** Callback when unit changes */
    onValueChange: (unit: string) => void;
    /** Additional class for the trigger button */
    class?: string;
  }

  let { value, onValueChange, class: className }: Props = $props();

  let open = $state(false);
  let triggerRef = $state<HTMLButtonElement>(null!);

  const displayLabel = $derived(value || "Select unit...");

  function closeAndFocus() {
    open = false;
    tick().then(() => triggerRef?.focus());
  }

  function selectUnit(unit: string) {
    onValueChange(unit);
    closeAndFocus();
  }
</script>

<Popover.Root bind:open>
  <Popover.Trigger bind:ref={triggerRef}>
    {#snippet child({ props })}
      <Button
        variant="outline"
        class={cn("w-full justify-between", className)}
        {...props}
        role="combobox"
        aria-expanded={open}
      >
        {displayLabel}
        <ChevronsUpDown class="ml-2 size-4 shrink-0 opacity-50" />
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-(--bits-popover-anchor-width) p-0">
    <Command.Root>
      <Command.Input placeholder="Search units..." />
      <Command.List>
        <Command.Empty>No unit found.</Command.Empty>
        <Command.Group>
          {#each FOOD_UNITS as unit}
            <Command.Item value={unit} onSelect={() => selectUnit(unit)}>
              <Check
                class={cn("mr-2 size-4", value !== unit && "text-transparent")}
              />
              {unit}
            </Command.Item>
          {/each}
        </Command.Group>
      </Command.List>
    </Command.Root>
  </Popover.Content>
</Popover.Root>
