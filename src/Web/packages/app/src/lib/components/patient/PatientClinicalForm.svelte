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
  const state = new ClinicalState(() => formEl);

  $effect(() => {
    onstate?.(state);
  });
</script>

<form
  id="clinical-form"
  bind:this={formEl}
  {...state.guard.enhance()}
>
  <!-- Hidden fields for read-only record data -->
  {#if state.record?.id}
    <input type="hidden" name="id" value={state.record.id} />
  {/if}
  {#if state.record?.avatarUrl}
    <input type="hidden" name="avatarUrl" value={state.record.avatarUrl} />
  {/if}
  {#if state.record?.createdAt}
    <input type="hidden" name="createdAt" value={state.record.createdAt instanceof Date ? state.record.createdAt.toISOString() : state.record.createdAt} />
  {/if}
  {#if state.record?.modifiedAt}
    <input type="hidden" name="modifiedAt" value={state.record.modifiedAt instanceof Date ? state.record.modifiedAt.toISOString() : state.record.modifiedAt} />
  {/if}

  <div class="grid gap-4 sm:grid-cols-2">
    <div class="space-y-2">
      <Label for="diabetes-type">Diabetes Type</Label>
      <Select.Root type="single" name="diabetesType" bind:value={state.diabetesType}>
        <Select.Trigger id="diabetes-type" aria-invalid={state.guard.issuesFor("diabetesType").length > 0}>
          {state.diabetesType
            ? (diabetesTypeLabels[state.diabetesType as DiabetesType] ?? state.diabetesType)
            : "Select type"}
        </Select.Trigger>
        <Select.Content>
          {#each Object.entries(diabetesTypeLabels) as [value, label]}
            <Select.Item {value} {label} />
          {/each}
        </Select.Content>
      </Select.Root>
      {#each state.guard.issuesFor("diabetesType") as issue}
        <p class="text-sm text-destructive">{issue.message}</p>
      {/each}
    </div>

    {#if state.diabetesType === DiabetesType.Other}
      <div class="space-y-2">
        <Label for="diabetes-type-other">Specify Type</Label>
        <Input
          id="diabetes-type-other"
          name="diabetesTypeOther"
          bind:value={state.diabetesTypeOther}
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
        bind:value={state.diagnosisDate}
      />
    </div>

    <div class="space-y-2">
      <Label for="date-of-birth">Date of Birth</Label>
      <Input
        id="date-of-birth"
        name="dateOfBirth"
        type="date"
        bind:value={state.dateOfBirth}
      />
    </div>

    <div class="space-y-2">
      <Label for="preferred-name">Preferred Name</Label>
      <Input
        id="preferred-name"
        name="preferredName"
        bind:value={state.preferredName}
        placeholder="How you'd like to be addressed"
      />
    </div>

    <div class="space-y-2">
      <Label for="pronouns">Pronouns</Label>
      <Input
        id="pronouns"
        name="pronouns"
        bind:value={state.pronouns}
        placeholder="e.g. she/her, he/him, they/them"
      />
    </div>
  </div>
</form>
