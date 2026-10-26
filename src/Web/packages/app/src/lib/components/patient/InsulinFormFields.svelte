<script lang="ts">
  import { labelFor } from "#lib/components/ui/enum-value.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Checkbox } from "#lib/components/ui/checkbox/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import * as Select from "#lib/components/ui/select/index.js";
  import { type InsulinFormulation } from "#lib/api/index.js";
  import type { createInsulin, updateInsulin } from "#lib/api/generated/patientRecords.generated.remote.js";
  import {
    insulinCategoryLabels,
    insulinCategoryDescriptions,
    insulinRoleLabels,
    insulinRoleDescriptions,
  } from "./labels";

  interface Props {
    /** The fields of the form the controls submit to: updateInsulin nests them under `request`. */
    fields: typeof createInsulin.fields | typeof updateInsulin.fields.request;

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
    fields,
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
      description: labelFor(insulinCategoryDescriptions, value) ?? "",
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
        name={fields.insulinCategory.as("select").name}
        bind:value={category}
        onValueChange={handleCategoryChange}
      >
        <Select.Trigger id="insulin-category">
          {category
            ? (labelFor(insulinCategoryLabels, category) ?? category)
            : "Select category"}
        </Select.Trigger>
        <Select.Content>
          {#each insulinCategoryItems as cat (cat.value)}
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
            {#each formulations as f (f.id)}
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
      <Label for="insulin-name">Brand / name</Label>
      <Input
        name={fields.name.as("text").name}
        id="insulin-name"
        bind:value={name}
        placeholder="e.g. Humalog, Lantus, Fiasp"
        readonly={!!formulationId}
      />
    </div>

    <div class="space-y-2">
      <Label for="insulin-role">Role</Label>
      <Select.Root type="single" name={fields.role.as("select").name} bind:value={role}>
        <Select.Trigger id="insulin-role">
          {labelFor(insulinRoleLabels, role) ?? role}
        </Select.Trigger>
        <Select.Content>
          {#each Object.entries(insulinRoleLabels) as [value, label] (value)}
            <Select.Item {value} {label}>
              <div>
                <div>{label}</div>
                <div class="text-xs text-muted-foreground">
                  {labelFor(insulinRoleDescriptions, value) ?? ""}
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
      <Label for="insulin-dia">Duration of insulin action</Label>
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
        <Label for="insulin-start">Start date</Label>
        <Input
          name={fields.startDate.as("date").name}
          id="insulin-start"
          type="date"
          bind:value={startDate}
        />
      </div>
      <div class="space-y-2">
        <Label for="insulin-end">End date</Label>
        <Input
          name={fields.endDate.as("date").name}
          id="insulin-end"
          type="date"
          bind:value={endDate}
        />
      </div>
    </div>

    <!-- Checkboxes -->
    <div class="flex items-center gap-4">
      <div class="flex items-center gap-2">
        <Checkbox
          id="insulin-current"
          name={fields.isCurrent.as("checkbox").name}
          bind:checked={isCurrent}
        />
        <Label for="insulin-current">Currently in use</Label>
      </div>

      <div class="flex items-center gap-2">
        <Checkbox
          id="insulin-primary"
          name={fields.isPrimary.as("checkbox").name}
          bind:checked={isPrimary}
        />
        <Label for="insulin-primary">Primary for this role</Label>
      </div>
    </div>

    <!-- Notes -->
    <div class="space-y-2">
      <Label for="insulin-notes">Notes</Label>
      <Textarea
        name={fields.notes.as("text").name}
        id="insulin-notes"
        bind:value={notes}
        placeholder="Any additional notes about this insulin"
        rows={2}
      />
    </div>
  {/if}
</div>
