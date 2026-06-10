<script lang="ts">
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import * as Select from "$lib/components/ui/select";
  import { DiabetesType } from "$api";
  import { diabetesTypeLabels } from "./labels";
  import { ClinicalState } from "./state.svelte";

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
  {...clinical.guard.enhance()}
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
    <div class="space-y-2">
      <Label for="diabetes-type">Diabetes Type</Label>
      <Select.Root type="single" name="diabetesType" bind:value={clinical.diabetesType}>
        <Select.Trigger id="diabetes-type" aria-invalid={clinical.guard.issuesFor("diabetesType").length > 0}>
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
      {#each clinical.guard.issuesFor("diabetesType") as issue}
        <p class="text-sm text-destructive">{issue.message}</p>
      {/each}
    </div>

    {#if clinical.diabetesType === DiabetesType.Other}
      <div class="space-y-2">
        <Label for="diabetes-type-other">Specify Type</Label>
        <Input
          id="diabetes-type-other"
          name="diabetesTypeOther"
          bind:value={clinical.diabetesTypeOther}
          placeholder="e.g. Type 3c"
        />
      </div>
    {/if}

    <div class="space-y-2">
      <Label for="diagnosis-date">Diagnosis Date</Label>
      <Input
        id="diagnosis-date"
        name="diagnosisDate"
        type="date"
        bind:value={clinical.diagnosisDate}
      />
    </div>

    <div class="space-y-2">
      <Label for="date-of-birth">Date of Birth</Label>
      <Input
        id="date-of-birth"
        name="dateOfBirth"
        type="date"
        bind:value={clinical.dateOfBirth}
      />
    </div>

    <div class="space-y-2">
      <Label for="preferred-name">Preferred Name</Label>
      <Input
        id="preferred-name"
        name="preferredName"
        bind:value={clinical.preferredName}
        placeholder="How you'd like to be addressed"
      />
    </div>

    <div class="space-y-2">
      <Label for="pronouns">Pronouns</Label>
      <Input
        id="pronouns"
        name="pronouns"
        bind:value={clinical.pronouns}
        placeholder="e.g. she/her, he/him, they/them"
      />
    </div>

    <div class="space-y-2 @sm:col-span-2">
      <Label for="timezone">Timezone</Label>
      <Input
        id="timezone"
        name="timezone"
        bind:value={clinical.timezone}
        placeholder="e.g. Australia/Sydney"
      />
      {#if clinical.timezoneAutoDetected}
        <p class="text-xs text-muted-foreground">
          Auto-detected from your browser. Save to confirm — alerts with time-of-day rules use this to interpret window hours in your local time.
        </p>
      {:else}
        <p class="text-xs text-muted-foreground">
          IANA timezone id (e.g. Europe/London). Used by alerts, schedules, and analytics.
        </p>
      {/if}
    </div>
  </div>
</form>
