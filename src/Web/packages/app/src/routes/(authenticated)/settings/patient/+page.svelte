<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    HeartPulse,
    Cpu,
    Syringe,
    Save,
    Loader2,
  } from "lucide-svelte";
  import {
    PatientClinicalForm,
    PatientDeviceManager,
    PatientInsulinManager,
  } from "$lib/components/patient";
  import { coachmark } from "@nocturne/coach";
  import * as patientRemote from "$api/generated/patientRecords.generated.remote";

  const patientRecord = patientRemote.getPatientRecord();
  const devices = patientRemote.getDevices();
  const insulins = patientRemote.getInsulins();

  const patientConfigured = $derived(!!patientRecord.current?.diabetesType);
  const devicesConfigured = $derived(
    (devices.current ?? []).some((d) => d.isCurrent === true),
  );
  const insulinsConfigured = $derived(
    (insulins.current ?? []).some((i) => i.isCurrent === true),
  );

  let saving = $state(false);
  let saveFn = $state<(() => Promise<boolean>) | undefined>(undefined);

  function handleState(api: { save: () => Promise<boolean>; saving: boolean; isValid: boolean }) {
    saving = api.saving;
    saveFn = api.save;
  }

  async function handleSave() {
    if (saveFn) await saveFn();
  }
</script>

<svelte:head>
  <title>Patient Record - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
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
  <Card.Root {@attach coachmark({ key: "onboarding.patient-details", title: "Why this matters", description: "Your diabetes type determines how Nocturne calculates bolus suggestions and categorizes treatments. Select your type and save.", completedWhen: () => patientConfigured })}>
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
      <Button onclick={handleSave} disabled={saving}>
        {#if saving}
          <Loader2 class="mr-2 h-4 w-4 animate-spin" />
        {:else}
          <Save class="mr-2 h-4 w-4" />
        {/if}
        Save Changes
      </Button>
    </Card.Footer>
  </Card.Root>

  <!-- Devices -->
  <Card.Root {@attach coachmark({ key: "onboarding.devices", title: "Add your current device", description: "Add the CGM, pump, or meter you use right now and mark it as current. Historical devices can be added later.", completedWhen: () => devicesConfigured })}>
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
  <Card.Root {@attach coachmark({ key: "onboarding.insulins", title: "Add your current insulin", description: "Add at least one insulin and mark it as current. The brand and type help Nocturne estimate active insulin curves.", completedWhen: () => insulinsConfigured })}>
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
