<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Check, ChevronsUpDown } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { tick } from "svelte";
  import { GI_OPTIONS, getGiLabel } from "./food-constants";

  interface Props {
    /** Currently selected GI value */
    value: number;
    /** Callback when GI changes */
    onValueChange: (gi: number) => void;
    /** Additional class for the trigger button */
    class?: string;
  }

  let { value, onValueChange, class: className }: Props = $props();

  let open = $state(false);
  let triggerRef = $state<HTMLButtonElement | null>(null);

  const displayLabel = $derived(getGiLabel(value) || "Select GI...");

  function closeAndFocus() {
    open = false;
    tick().then(() => triggerRef?.focus());
  }

  function selectGi(gi: number) {
    onValueChange(gi);
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
      <Command.Input placeholder="Search GI..." />
      <Command.List>
        <Command.Empty>No GI level found.</Command.Empty>
        <Command.Group>
          {#each GI_OPTIONS as option}
            <Command.Item
              value={option.label}
              onSelect={() => selectGi(option.value)}
            >
              <Check
                class={cn(
                  "mr-2 size-4",
                  value !== option.value && "text-transparent"
                )}
              />
              {option.label}
            </Command.Item>
          {/each}
        </Command.Group>
      </Command.List>
    </Command.Root>
  </Popover.Content>
</Popover.Root>
