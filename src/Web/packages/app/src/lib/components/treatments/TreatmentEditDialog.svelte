<script lang="ts">
  import type { Treatment } from "$lib/api";
  import * as Dialog from "$lib/components/ui/dialog";
  import * as Command from "$lib/components/ui/command";
  import * as Popover from "$lib/components/ui/popover";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import { Badge } from "$lib/components/ui/badge";
  import {
    Syringe,
    Apple,
    Clock,
    User,
    MessageSquare,
    Database,
    Check,
    ChevronsUpDown,
    Braces,
  } from "lucide-svelte";
  import {
    getEventTypeStyle,
    TREATMENT_CATEGORIES,
  } from "$lib/constants/treatment-categories";
  import { cn } from "$lib/utils";
  import { formatDateTime } from "$lib/utils/date-formatting";

  interface Props {
    open: boolean;
    treatment: Treatment | null;
    availableEventTypes?: string[];
    isLoading?: boolean;
    onClose: () => void;
    onSave: (treatment: Treatment) => void;
  }

  let {
    open = $bindable(),
    treatment,
    availableEventTypes = [],
    isLoading = false,
    onClose,
    onSave,
  }: Props = $props();

  $inspect(treatment);
  // Combobox state
  let eventTypePopoverOpen = $state(false);

  // Editable form state
  let formState = $state<{
    eventType: string;
    insulin: number | undefined;
    carbs: number | undefined;
    glucose: number | undefined;
    duration: number | undefined;
    notes: string;
    profile: string;
    additionalPropertiesJson: string;
  }>({
    eventType: "",
    insulin: undefined,
    carbs: undefined,
    glucose: undefined,
    duration: undefined,
    notes: "",
    profile: "",
    additionalPropertiesJson: "",
  });

  // Get all known event types from categories + available from data
  let allEventTypes = $derived.by(() => {
    const categoryTypes = Object.values(TREATMENT_CATEGORIES).flatMap(
      (cat) => cat.eventTypes as readonly string[]
    );
    const combined = new Set([...categoryTypes, ...availableEventTypes]);
    return Array.from(combined).sort();
  });

  // Filter event types based on search
  let eventTypeSearch = $state("");
  let filteredEventTypes = $derived.by(() => {
    if (!eventTypeSearch.trim()) return allEventTypes;
    const search = eventTypeSearch.toLowerCase();
    return allEventTypes.filter((type) => type.toLowerCase().includes(search));
  });

  // Reset form when treatment changes
  $effect(() => {
    if (treatment) {
      formState = {
        eventType: treatment.eventType || "",
        insulin: treatment.insulin ?? undefined,
        carbs: treatment.carbs ?? undefined,
        glucose: treatment.glucose ?? undefined,
        duration: treatment.duration ?? undefined,
        notes: treatment.notes || "",
        profile: treatment.profile || "",
        additionalPropertiesJson: treatment.additional_properties
          ? JSON.stringify(treatment.additional_properties, null, 2)
          : "",
      };
    }
  });

  // Check if additional_properties JSON is valid
  let additionalPropertiesError = $derived.by(() => {
    if (!formState.additionalPropertiesJson.trim()) return null;
    try {
      JSON.parse(formState.additionalPropertiesJson);
      return null;
    } catch (e) {
      return "Invalid JSON";
    }
  });

  function handleSubmit() {
    if (!treatment) return;
    if (additionalPropertiesError) return;

    let additionalProps: Record<string, unknown> | undefined = undefined;
    if (formState.additionalPropertiesJson.trim()) {
      try {
        additionalProps = JSON.parse(formState.additionalPropertiesJson);
      } catch {
        // Already validated above
      }
    }

    const updated: Treatment = {
      ...treatment,
      eventType: formState.eventType || undefined,
      insulin: formState.insulin || undefined,
      carbs: formState.carbs || undefined,
      glucose: formState.glucose || undefined,
      duration: formState.duration || undefined,
      notes: formState.notes || undefined,
      profile: formState.profile || undefined,
      additional_properties: additionalProps,
    };

    onSave(updated);
  }

  function selectEventType(type: string) {
    formState.eventType = type;
    eventTypePopoverOpen = false;
  }

  let style = $derived(getEventTypeStyle(formState.eventType));
  let hasAdditionalProperties = $derived(
    treatment?.additional_properties &&
      Object.keys(treatment.additional_properties).length > 0
  );
</script>

<Dialog.Root bind:open onOpenChange={(o) => !o && onClose()}>
  <Dialog.Content class="max-w-lg">
    <Dialog.Header>
      <Dialog.Title class="flex items-center gap-2">
        Edit Treatment
        {#if formState.eventType}
          <Badge variant="outline" class={style.colorClass}>
            {formState.eventType}
          </Badge>
        {/if}
      </Dialog.Title>
      <Dialog.Description>
        Edit the details of this treatment entry. Changes will be saved to the
        database.
      </Dialog.Description>
    </Dialog.Header>

    {#if treatment}
      <form
        onsubmit={(e) => {
          e.preventDefault();
          handleSubmit();
        }}
        class="space-y-4"
      >
        <!-- Read-only metadata -->
        <div
          class="flex flex-wrap gap-4 text-sm text-muted-foreground bg-muted/30 rounded-lg p-3"
        >
          <div class="flex items-center gap-1.5">
            <Clock class="h-3.5 w-3.5" />
            <span>{formatDateTime(treatment.created_at)}</span>
          </div>
          {#if treatment.enteredBy}
            <div class="flex items-center gap-1.5">
              <User class="h-3.5 w-3.5" />
              <span>{treatment.enteredBy}</span>
            </div>
          {/if}
          {#if treatment.source}
            <div class="flex items-center gap-1.5">
              <Database class="h-3.5 w-3.5" />
              <span>{treatment.source}</span>
            </div>
          {/if}
        </div>

        <!-- Event Type Combobox -->
        <div class="space-y-2">
          <Label>Event Type</Label>
          <Popover.Root bind:open={eventTypePopoverOpen}>
            <Popover.Trigger>
              {#snippet child({ props })}
                <Button
                  variant="outline"
                  role="combobox"
                  aria-expanded={eventTypePopoverOpen}
                  class="w-full justify-between font-normal"
                  {...props}
                >
                  {#if formState.eventType}
                    <span class={style.colorClass}>{formState.eventType}</span>
                  {:else}
                    <span class="text-muted-foreground">
                      Select or enter event type...
                    </span>
                  {/if}
                  <ChevronsUpDown class="ml-2 h-4 w-4 shrink-0 opacity-50" />
                </Button>
              {/snippet}
            </Popover.Trigger>
            <Popover.Content class="w-[300px] p-0" align="start">
              <Command.Root shouldFilter={false}>
                <Command.Input
                  placeholder="Search or enter custom type..."
                  bind:value={eventTypeSearch}
                />
                <Command.List>
                  <Command.Empty>
                    {#if eventTypeSearch.trim()}
                      <button
                        type="button"
                        class="w-full p-2 text-left text-sm hover:bg-accent"
                        onclick={() => selectEventType(eventTypeSearch.trim())}
                      >
                        Use "{eventTypeSearch.trim()}" as custom type
                      </button>
                    {:else}
                      No event types found.
                    {/if}
                  </Command.Empty>
                  <Command.Group>
                    {#each filteredEventTypes as type}
                      {@const typeStyle = getEventTypeStyle(type)}
                      <Command.Item
                        value={type}
                        onSelect={() => selectEventType(type)}
                        class="cursor-pointer"
                      >
                        <Check
                          class={cn(
                            "mr-2 h-4 w-4",
                            formState.eventType === type
                              ? "opacity-100"
                              : "opacity-0"
                          )}
                        />
                        <span class={typeStyle.colorClass}>{type}</span>
                      </Command.Item>
                    {/each}
                  </Command.Group>
                </Command.List>
              </Command.Root>
            </Popover.Content>
          </Popover.Root>
        </div>

        <!-- Insulin & Carbs Row -->
        <div class="grid grid-cols-2 gap-4">
          <div class="space-y-2">
            <Label for="insulin" class="flex items-center gap-1.5">
              <Syringe class="h-3.5 w-3.5 text-blue-500" />
              Insulin (U)
            </Label>
            <Input
              id="insulin"
              type="number"
              step="0.1"
              min="0"
              bind:value={formState.insulin}
              placeholder="0.0"
            />
          </div>

          <div class="space-y-2">
            <Label for="carbs" class="flex items-center gap-1.5">
              <Apple class="h-3.5 w-3.5 text-green-500" />
              Carbs (g)
            </Label>
            <Input
              id="carbs"
              type="number"
              step="1"
              min="0"
              bind:value={formState.carbs}
              placeholder="0"
            />
          </div>
        </div>

        <!-- Glucose & Duration Row -->
        <div class="grid grid-cols-2 gap-4">
          <div class="space-y-2">
            <Label for="glucose">Blood Glucose</Label>
            <Input
              id="glucose"
              type="number"
              step="1"
              min="0"
              bind:value={formState.glucose}
              placeholder="mg/dL"
            />
          </div>

          <div class="space-y-2">
            <Label for="duration">Duration (min)</Label>
            <Input
              id="duration"
              type="number"
              step="1"
              min="0"
              bind:value={formState.duration}
              placeholder="0"
            />
          </div>
        </div>

        <!-- Profile -->
        <div class="space-y-2">
          <Label for="profile">Profile</Label>
          <Input
            id="profile"
            bind:value={formState.profile}
            placeholder="Profile name"
          />
        </div>

        <!-- Notes -->
        <div class="space-y-2">
          <Label for="notes" class="flex items-center gap-1.5">
            <MessageSquare class="h-3.5 w-3.5" />
            Notes
          </Label>
          <Textarea
            id="notes"
            bind:value={formState.notes}
            placeholder="Add notes..."
            rows={3}
          />
        </div>

        <!-- Additional Properties -->
        {#if hasAdditionalProperties || formState.additionalPropertiesJson.trim()}
          <div class="space-y-2">
            <Label for="additionalProperties" class="flex items-center gap-1.5">
              <Braces class="h-3.5 w-3.5" />
              Additional Properties
              {#if additionalPropertiesError}
                <Badge variant="destructive" class="text-xs">
                  {additionalPropertiesError}
                </Badge>
              {/if}
            </Label>
            <Textarea
              id="additionalProperties"
              bind:value={formState.additionalPropertiesJson}
              placeholder="&#123;&#125;"
              rows={5}
              class="font-mono text-xs {additionalPropertiesError
                ? 'border-destructive'
                : ''}"
            />
          </div>
        {/if}

        <Dialog.Footer class="gap-2">
          <Button
            type="button"
            variant="outline"
            onclick={onClose}
            disabled={isLoading}
          >
            Cancel
          </Button>
          <Button type="submit" disabled={isLoading}>
            {isLoading ? "Saving..." : "Save Changes"}
          </Button>
        </Dialog.Footer>
      </form>
    {/if}
  </Dialog.Content>
</Dialog.Root>
