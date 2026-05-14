<script lang="ts">
  import type { DeviceEventType } from "$lib/api";
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Check, ChevronsUpDown, Smartphone } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import {
    DEVICE_EVENT_TYPES,
    getDeviceEventTypeLabel,
  } from "$lib/constants/device-event-types";

  interface Props {
    form: {
      eventType: DeviceEventType | undefined;
      notes: string;
    };
  }

  let { form = $bindable() }: Props = $props();

  let popoverOpen = $state(false);
  let searchValue = $state("");

  let filtered = $derived.by(() => {
    if (!searchValue.trim()) return DEVICE_EVENT_TYPES;
    const q = searchValue.toLowerCase();
    return DEVICE_EVENT_TYPES.filter(
      (t) =>
        t.toLowerCase().includes(q) ||
        getDeviceEventTypeLabel(t).toLowerCase().includes(q)
    );
  });

  function select(type: DeviceEventType) {
    form.eventType = type;
    popoverOpen = false;
    searchValue = "";
  }
</script>

<div class="space-y-2">
  <Label class="flex items-center gap-1.5">
    <Smartphone class="h-3.5 w-3.5 text-orange-500" />
    Event Type
  </Label>
  <Popover.Root bind:open={popoverOpen}>
    <Popover.Trigger>
      {#snippet child({ props })}
        <Button
          variant="outline"
          role="combobox"
          aria-expanded={popoverOpen}
          class="w-full justify-between font-normal"
          {...props}
        >
          {#if form.eventType}
            <span>{getDeviceEventTypeLabel(form.eventType)}</span>
          {:else}
            <span class="text-muted-foreground">Select event type...</span>
          {/if}
          <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
        </Button>
      {/snippet}
    </Popover.Trigger>
    <Popover.Content class="w-[--bits-popover-anchor-width] p-0" align="start">
      <Command.Root shouldFilter={false}>
        <Command.Input
          placeholder="Search event types..."
          bind:value={searchValue}
        />
        <Command.List class="max-h-52">
          <Command.Empty>No event types found.</Command.Empty>
          <Command.Group>
            {#each filtered as type (type)}
              <Command.Item
                value={type}
                onSelect={() => select(type)}
                class="cursor-pointer"
              >
                <Check
                  class={cn(
                    "mr-2 h-4 w-4",
                    form.eventType === type ? "opacity-100" : "opacity-0"
                  )}
                />
                {getDeviceEventTypeLabel(type)}
              </Command.Item>
            {/each}
          </Command.Group>
        </Command.List>
      </Command.Root>
    </Popover.Content>
  </Popover.Root>
</div>

<div class="space-y-2">
  <Label for="deviceNotes">Notes</Label>
  <Textarea
    id="deviceNotes"
    bind:value={form.notes}
    placeholder="Additional notes..."
    rows={3}
  />
</div>
