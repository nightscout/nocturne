<script lang="ts">
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Check, ChevronsUpDown } from "lucide-svelte";
  import { cn } from "$lib/utils";

  interface Props {
    /** Currently selected IANA timezone id */
    value?: string;
    id?: string;
    placeholder?: string;
    disabled?: boolean;
    class?: string;
    "aria-invalid"?: boolean | "true" | "false";
    "aria-describedby"?: string;
  }

  let {
    value = $bindable(),
    id,
    placeholder = "Select timezone...",
    disabled = false,
    class: className,
    "aria-invalid": ariaInvalid,
    "aria-describedby": ariaDescribedby,
  }: Props = $props();

  const allTimezones: string[] =
    typeof Intl !== "undefined" && "supportedValuesOf" in Intl
      ? (Intl as unknown as { supportedValuesOf(key: string): string[] }).supportedValuesOf("timeZone")
      : [];

  let popoverOpen = $state(false);
  let searchValue = $state("");

  let filteredTimezones = $derived.by(() => {
    if (!searchValue.trim()) return allTimezones;
    const search = searchValue.toLowerCase();
    return allTimezones.filter((tz) => tz.toLowerCase().includes(search));
  });

  function selectTimezone(tz: string) {
    value = tz;
    popoverOpen = false;
    searchValue = "";
  }
</script>

<Popover.Root bind:open={popoverOpen}>
  <Popover.Trigger>
    <!-- The caller's id is applied after Popover's own props: Popover.Trigger
         generates an id of its own, and whichever comes last wins. A label's
         `for` targets the caller's id. -->
    {#snippet child({ props }: { props: Record<string, unknown> })}
      <Button
        variant="outline"
        role="combobox"
        aria-expanded={popoverOpen}
        aria-invalid={ariaInvalid}
        aria-describedby={ariaDescribedby}
        class={cn("w-full justify-between font-normal", className)}
        {disabled}
        {...props}
        {...id ? { id } : {}}
      >
        {#if value}
          <span>{value}</span>
        {:else}
          <span class="text-muted-foreground">{placeholder}</span>
        {/if}
        <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
      </Button>
    {/snippet}
  </Popover.Trigger>
  <Popover.Content class="w-[300px] p-0" align="start">
    <Command.Root shouldFilter={false}>
      <Command.Input placeholder="Search timezones..." bind:value={searchValue} />
      <Command.List>
        <Command.Empty>No timezone found.</Command.Empty>
        <Command.Group>
          {#each filteredTimezones as tz}
            <Command.Item
              value={tz}
              onSelect={() => selectTimezone(tz)}
              class="cursor-pointer"
            >
              <Check
                class={cn(
                  "mr-2 h-4 w-4",
                  value === tz ? "opacity-100" : "opacity-0"
                )}
              />
              {tz}
            </Command.Item>
          {/each}
        </Command.Group>
      </Command.List>
    </Command.Root>
  </Popover.Content>
</Popover.Root>
