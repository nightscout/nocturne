<script lang="ts">
  import { Label } from "$lib/components/ui/label";
  import { Input } from "$lib/components/ui/input";
  import { Textarea } from "$lib/components/ui/textarea";
  import * as Select from "$lib/components/ui/select";
  import {
    type InsulinCategory,
    type InsulinFormulation,
    type InsulinRole,
  } from "$api";
  import {
    insulinCategoryLabels,
    insulinCategoryDescriptions,
    insulinRoleLabels,
    insulinRoleDescriptions,
  } from "./labels";

  interface Props {
    // Core fields (bindable)
    category?: string;
    formulationId?: string;
    name?: string;
    role?: string;
    dia?: number | null;
    peak?: number | null;
    concentration?: number | null;

    // Data
    formulations?: InsulinFormulation[];
    catalog?: InsulinFormulation[];

    // Extended fields (optional, for dialog variant, bindable)
    startDate?: string;
    endDate?: string;
    isCurrent?: boolean;
    isPrimary?: boolean;
    notes?: string;

    // Control
    showExtendedFields?: boolean;

    // Callbacks
    onCategoryChange?: () => void;
    onFormulationChange?: () => void;
  }

  let {
    category = $bindable(""),
    formulationId = $bindable(""),
    name = $bindable(""),
    role = $bindable(""),
    dia = $bindable(4.0),
    peak = $bindable(75),
    concentration = $bindable(100),
    formulations = [],
    catalog = [],
    startDate = $bindable(""),
    endDate = $bindable(""),
    isCurrent = $bindable(true),
    isPrimary = $bindable(false),
    notes = $bindable(""),
    showExtendedFields = false,
    onCategoryChange,
    onFormulationChange,
  }: Props = $props();

  // Category items with descriptions
  const insulinCategoryItems = $derived(
    Object.entries(insulinCategoryLabels).map(([value, label]) => ({
      value,
      label,
      description: insulinCategoryDescriptions[value as InsulinCategory] ?? "",
    })),
  );

  function handleCategoryChange() {
    onCategoryChange?.();
  }

  function handleFormulationChange() {
    onFormulationChange?.();
  }
</script>

<div class="@container space-y-4">
  <!-- Category and Formulation Select -->
  <div class="grid gap-4 @sm:grid-cols-2">
    <div class="space-y-2">
      <Label for="insulin-category">Category</Label>
      <Select.Root
        type="single"
        name="insulinCategory"
        bind:value={category}
        onValueChange={handleCategoryChange}
      >
        <Select.Trigger id="insulin-category">
          {category
            ? (insulinCategoryLabels[category as InsulinCategory] ?? category)
            : "Select category"}
        </Select.Trigger>
        <Select.Content>
          {#each insulinCategoryItems as cat}
            <Select.Item value={cat.value} label={cat.label}>
              <div>
                <div>{cat.label}</div>
                <div class="text-xs text-muted-foreground">{cat.description}</div>
              </div>
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>

    {#if category && formulations.length > 0}
      <div class="space-y-2">
        <Label for="insulin-formulation">Formulation</Label>
        <Select.Root
          type="single"
          bind:value={formulationId}
          onValueChange={handleFormulationChange}
        >
          <Select.Trigger id="insulin-formulation">
            {formulationId
              ? (catalog.find(f => f.id === formulationId)?.name ?? "Select formulation")
              : "Select formulation"}
          </Select.Trigger>
          <Select.Content>
            {#each formulations as f}
              <Select.Item value={f.id ?? ""} label={f.name ?? ""}>
                <div>
                  <div>{f.name}</div>
                  <div class="text-xs text-muted-foreground">
                    {#if f.defaultDia && f.defaultPeak}
                      DIA {f.defaultDia}h &middot; Peak {f.defaultPeak}min &middot; U-{f.concentration}
                    {:else if f.defaultDia}
                      DIA {f.defaultDia}h &middot; U-{f.concentration}
                    {:else}
                      U-{f.concentration}
                    {/if}
                  </div>
                </div>
              </Select.Item>
            {/each}
            <Select.Item value="" label="Custom">
              <div>
                <div>Custom</div>
                <div class="text-xs text-muted-foreground">Enter a custom insulin name</div>
              </div>
            </Select.Item>
          </Select.Content>
        </Select.Root>
      </div>
    {/if}
  </div>

  <!-- Name and Role Select -->
  <div class="grid gap-4 @sm:grid-cols-2">
    <div class="space-y-2">
      <Label for="insulin-name">Brand / Name</Label>
      <Input
        name="name"
        id="insulin-name"
        bind:value={name}
        placeholder="e.g. Humalog, Lantus, Fiasp"
        readonly={!!formulationId}
      />
    </div>

    <div class="space-y-2">
      <Label for="insulin-role">Role</Label>
      <Select.Root type="single" name="role" bind:value={role}>
        <Select.Trigger id="insulin-role">
          {insulinRoleLabels[role as InsulinRole] ?? role}
        </Select.Trigger>
        <Select.Content>
          {#each Object.entries(insulinRoleLabels) as [value, label]}
            <Select.Item {value} {label}>
              <div>
                <div>{label}</div>
                <div class="text-xs text-muted-foreground">
                  {insulinRoleDescriptions[value as InsulinRole] ?? ""}
                </div>
              </div>
            </Select.Item>
          {/each}
        </Select.Content>
      </Select.Root>
    </div>
  </div>

  <!-- DIA, Peak, Concentration -->
  <div class="grid gap-4 @sm:grid-cols-3">
    <div class="space-y-2">
      <Label for="insulin-dia">Duration of Insulin Action</Label>
      <div class="flex items-center gap-2">
        <Input
          id="insulin-dia"
          type="number"
          bind:value={dia}
          step={0.5}
          min={0.5}
          max={48}
          class="flex-1"
        />
        <span class="text-sm text-muted-foreground whitespace-nowrap">hours</span>
      </div>
    </div>

    <div class="space-y-2">
      <Label for="insulin-peak">Peak</Label>
      <div class="flex items-center gap-2">
        <Input
          id="insulin-peak"
          type="number"
          bind:value={peak}
          step={5}
          min={0}
          class="flex-1"
        />
        <span class="text-sm text-muted-foreground whitespace-nowrap">min</span>
      </div>
    </div>

    <div class="space-y-2">
      <Label for="insulin-concentration">Concentration</Label>
      <div class="flex items-center gap-2">
        <span class="text-sm text-muted-foreground whitespace-nowrap">U-</span>
        <Input
          id="insulin-concentration"
          type="number"
          bind:value={concentration}
          step={100}
          min={100}
          class="flex-1"
        />
      </div>
    </div>
  </div>

  {#if showExtendedFields}
    <!-- Start/End Dates -->
    <div class="grid gap-4 @sm:grid-cols-2">
      <div class="space-y-2">
        <Label for="insulin-start">Start Date</Label>
        <Input
          name="startDate"
          id="insulin-start"
          type="date"
          bind:value={startDate}
        />
      </div>
      <div class="space-y-2">
        <Label for="insulin-end">End Date</Label>
        <Input
          name="endDate"
          id="insulin-end"
          type="date"
          bind:value={endDate}
        />
      </div>
    </div>

    <!-- Checkboxes -->
    <div class="flex items-center gap-4">
      <div class="flex items-center gap-2">
        <input
          id="insulin-current"
          type="checkbox"
          name="isCurrent"
          bind:checked={isCurrent}
          class="h-4 w-4 rounded border-input"
        />
        <Label for="insulin-current">Currently in use</Label>
      </div>

      <div class="flex items-center gap-2">
        <input
          id="insulin-primary"
          type="checkbox"
          name="isPrimary"
          bind:checked={isPrimary}
          class="h-4 w-4 rounded border-input"
        />
        <Label for="insulin-primary">Primary for this role</Label>
      </div>
    </div>

    <!-- Notes -->
    <div class="space-y-2">
      <Label for="insulin-notes">Notes</Label>
      <Textarea
        name="notes"
        id="insulin-notes"
        bind:value={notes}
        placeholder="Any additional notes about this insulin"
        rows={2}
      />
    </div>
  {/if}
</div>
