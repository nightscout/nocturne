<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import * as Select from "$lib/components/ui/select";
  import { FormField } from "$lib/forms";
  import { DiabetesType, BiologicalSex } from "$api";
  import { diabetesTypeLabels, biologicalSexLabels } from "./labels";
  import { ClinicalState } from "./state.svelte";
  import TimezoneCombobox from "./TimezoneCombobox.svelte";

  interface Props {
    onstate?: (state: ClinicalState) => void;
  }

  let { onstate }: Props = $props();

  let formEl = $state<HTMLFormElement | null>(null);
  const clinical = new ClinicalState(() => formEl);

  $effect(() => {
    onstate?.(clinical);
  });
</script>

<form
  id="clinical-form"
  class="@container"
  bind:this={formEl}
  {...clinical.guard.enhance(async () => {
    await clinical.weight.save();
  })}
>
  <!-- Hidden fields for read-only record data -->
  {#if clinical.record?.id}
    <input type="hidden" name="id" value={clinical.record.id} />
  {/if}
  {#if clinical.record?.avatarUrl}
    <input type="hidden" name="avatarUrl" value={clinical.record.avatarUrl} />
  {/if}
  {#if clinical.record?.createdAt}
    <input type="hidden" name="createdAt" value={clinical.record.createdAt instanceof Date ? clinical.record.createdAt.toISOString() : clinical.record.createdAt} />
  {/if}
  {#if clinical.record?.modifiedAt}
    <input type="hidden" name="modifiedAt" value={clinical.record.modifiedAt instanceof Date ? clinical.record.modifiedAt.toISOString() : clinical.record.modifiedAt} />
  {/if}

  <div class="grid gap-4 @sm:grid-cols-2">
    <!-- aria-required, not required: bits-ui puts `required` on a 1px hidden
         input that still takes part in constraint validation, so an empty
         select blocks the submit event with a bubble anchored off-screen — the
         Save button would appear to do nothing. The requirement is enforced by
         the guard's schema, which reports it on the field. -->
    <FormField
      label="Diabetes Type"
      id="diabetes-type"
      required
      issues={clinical.guard.issuesFor("diabetesType")}
    >
      {#snippet control(field)}
        <Select.Root type="single" name="diabetesType" bind:value={clinical.diabetesType}>
          <Select.Trigger
            id={field.id}
            aria-required="true"
            aria-invalid={field["aria-invalid"]}
            aria-describedby={field["aria-describedby"]}
          >
            {clinical.diabetesType
              ? (diabetesTypeLabels[clinical.diabetesType as DiabetesType] ?? clinical.diabetesType)
              : "Select type"}
          </Select.Trigger>
          <Select.Content>
            {#each Object.entries(diabetesTypeLabels) as [value, label]}
              <Select.Item {value} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      {/snippet}
    </FormField>

    {#if clinical.diabetesType === DiabetesType.Other}
      <FormField label="Specify Type" id="diabetes-type-other">
        {#snippet control(field)}
          <Input
            {...field}
            name="diabetesTypeOther"
            bind:value={clinical.diabetesTypeOther}
            placeholder="e.g. Type 3c"
          />
        {/snippet}
      </FormField>
    {/if}

    <FormField label="Diagnosis Date" id="diagnosis-date">
      {#snippet control(field)}
        <Input
          {...field}
          name="diagnosisDate"
          type="date"
          autocomplete="off"
          bind:value={clinical.diagnosisDate}
        />
      {/snippet}
    </FormField>

    <FormField label="Date of Birth" id="date-of-birth">
      {#snippet control(field)}
        <Input
          {...field}
          name="dateOfBirth"
          type="date"
          autocomplete="bday"
          bind:value={clinical.dateOfBirth}
        />
      {/snippet}
    </FormField>

    <FormField
      label="Sex"
      id="sex"
      description="Biological sex, used with your age to show sex-specific typical ranges in sleep reports. Separate from pronouns."
    >
      {#snippet control(field)}
        <Select.Root type="single" name="sex" bind:value={clinical.sex}>
          <Select.Trigger {...field}>
            {clinical.sex
              ? (biologicalSexLabels[clinical.sex as BiologicalSex] ?? clinical.sex)
              : "Select sex"}
          </Select.Trigger>
          <Select.Content>
            <Select.Item value="" label="Prefer not to say" />
            {#each Object.entries(biologicalSexLabels) as [value, label]}
              <Select.Item {value} {label} />
            {/each}
          </Select.Content>
        </Select.Root>
      {/snippet}
    </FormField>

    <FormField label="Preferred Name" id="preferred-name">
      {#snippet control(field)}
        <Input
          {...field}
          name="preferredName"
          autocomplete="nickname"
          bind:value={clinical.preferredName}
          placeholder="How you'd like to be addressed"
        />
      {/snippet}
    </FormField>

    <FormField label="Pronouns" id="pronouns">
      {#snippet control(field)}
        <Input
          {...field}
          name="pronouns"
          bind:value={clinical.pronouns}
          placeholder="e.g. she/her, he/him, they/them"
        />
      {/snippet}
    </FormField>

    <FormField
      label="Timezone"
      id="timezone"
      class="@sm:col-span-2"
      issues={clinical.guard.issuesFor("timezone")}
    >
      {#snippet control(field)}
        <input type="hidden" name="timezone" value={clinical.timezone} />
        <TimezoneCombobox
          id={field.id}
          aria-invalid={field["aria-invalid"]}
          aria-describedby={field["aria-describedby"]}
          bind:value={clinical.timezone}
          placeholder="Search timezones..."
        />
      {/snippet}
      {#snippet hint()}
        {#if clinical.timezoneAutoDetected}
          <p class="text-xs text-muted-foreground">
            Auto-detected from your browser. Save to confirm — alerts with time-of-day rules use this to interpret window hours in your local time, starting from when you save.
          </p>
        {:else}
          <p class="text-xs text-muted-foreground">
            Used by alerts, schedules, and analytics. Changing it takes effect from when you save — past data isn't reinterpreted.
          </p>
        {/if}
      {/snippet}
    </FormField>

    <FormField
      label="Weight (kg)"
      id="weight"
      description="Recorded to your weight history when you save — only if it's changed."
      issues={clinical.weight.saveError}
    >
      {#snippet control(field)}
        <Input
          {...field}
          type="number"
          step="0.1"
          min="0"
          bind:value={clinical.weight.weightKg}
          placeholder="e.g. 70"
        />
      {/snippet}
    </FormField>
  </div>
</form>
