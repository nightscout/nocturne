<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { FormActions } from "$lib/forms";
  import HeartPulse from "@lucide/svelte/icons/heart-pulse";
  import Cpu from "@lucide/svelte/icons/cpu";
  import Syringe from "@lucide/svelte/icons/syringe";
  import {
    PatientClinicalForm,
    PatientDeviceManager,
    PatientInsulinManager,
  } from "$lib/components/patient";
  import type { ClinicalState } from "$lib/components/patient";

  let clinicalState = $state<ClinicalState | undefined>(undefined);

  function handleState(state: ClinicalState) {
    clinicalState = state;
  }
</script>

<svelte:head>
  <title>Patient Record - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <HeartPulse class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Patient Record</h1>
      <p class="text-muted-foreground">
        Manage your clinical information, devices, and insulins
      </p>
    </div>
  </div>

  <!-- Clinical Information -->
  <Card.Root>
    <Card.Header>
      <div class="flex items-center gap-2">
        <HeartPulse class="h-5 w-5 text-muted-foreground" />
        <Card.Title>Clinical Information</Card.Title>
      </div>
      <Card.Description>
        Basic information about your diabetes management
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-4">
      <PatientClinicalForm onstate={handleState} />
    </Card.Content>
    <Card.Footer class="border-t pt-6">
      <FormActions
        class="w-full"
        formId="clinical-form"
        submitLabel="Save Changes"
        form={clinicalState?.form}
        pending={!!clinicalState?.weight.saving}
        error={clinicalState?.guard.submitError}
        focusError
        disabled={!clinicalState?.record
          || (!clinicalState?.guard.dirty && !clinicalState?.weight.dirty)}
      />
    </Card.Footer>
  </Card.Root>

  <!-- Devices -->
  <Card.Root>
    <Card.Header>
      <div class="flex items-center gap-2">
        <Cpu class="h-5 w-5 text-muted-foreground" />
        <Card.Title>Devices</Card.Title>
      </div>
      <Card.Description>
        Pumps, CGMs, meters, and other devices you use
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <PatientDeviceManager variant="dialog" />
    </Card.Content>
  </Card.Root>

  <!-- Insulins -->
  <Card.Root>
    <Card.Header>
      <div class="flex items-center gap-2">
        <Syringe class="h-5 w-5 text-muted-foreground" />
        <Card.Title>Insulins</Card.Title>
      </div>
      <Card.Description>
        Insulin types and brands you use or have used
      </Card.Description>
    </Card.Header>
    <Card.Content>
      <PatientInsulinManager variant="dialog" />
    </Card.Content>
  </Card.Root>
</div>
