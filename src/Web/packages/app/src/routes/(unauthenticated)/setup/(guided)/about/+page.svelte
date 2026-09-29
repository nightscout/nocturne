<script lang="ts">
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { getSetupHub } from "$api/generated/setupHubs.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { PatientClinicalForm, type ClinicalState } from "$lib/components/patient";
  import { FormActions } from "$lib/forms";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";

  const relationshipQuery = getPatientRelationship();
  const voice = $derived(patientVoice(relationshipQuery.current));

  let clinical = $state<ClinicalState | undefined>(undefined);

  // The item is done once the diabetes type is saved, which the form requires; the preferred name
  // it may also have changed is what the patient voice reads.
  async function saved() {
    await Promise.all([getSetupHub().refresh(), relationshipQuery.refresh()]);
    await goto(resolve("/setup"));
  }
</script>

<div class="flex flex-col gap-6">
  <div class="flex flex-col gap-2 text-sm leading-relaxed text-muted-foreground" data-testid="about-intro">
    {#if voice.kind === "self"}
      <p>
        Printed reports show your name and date of birth from here, and sleep reports use your age
        and sex to show what is typical. Only your diabetes type is needed.
      </p>
      <p>Your timezone is already filled in from earlier.</p>
    {:else if voice.kind === "named"}
      <p>
        Printed reports show {voice.name}'s name and date of birth from here, and sleep reports use
        their age and sex to show what is typical. Only {voice.name}'s diabetes type is needed.
      </p>
      <p>The name and timezone you gave earlier are already filled in.</p>
    {:else}
      <p>
        Printed reports show the name and date of birth from here, and sleep reports use age and
        sex to show what is typical. Only the diabetes type is needed.
      </p>
      <p>The timezone you chose earlier is already filled in.</p>
    {/if}
  </div>

  <PatientClinicalForm onstate={(state) => (clinical = state)} onsaved={saved} />

  <FormActions
    formId="clinical-form"
    form={clinical?.form}
    pending={!!clinical?.weight.saving}
    error={clinical?.guard.submitError}
    focusError
    disabled={!clinical?.record}
  />
</div>
