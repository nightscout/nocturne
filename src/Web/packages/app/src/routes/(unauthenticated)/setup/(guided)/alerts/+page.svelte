<script lang="ts">
  import { getAlertSetup } from "$api/generated/setupAlerts.generated.remote";
  import { getPatientRelationship } from "$api/generated/tenantSettings.generated.remote";
  import { remoteErrorMessage } from "$lib/api/remote-error";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import AlertSetup from "./AlertSetup.svelte";

  const setupQuery = getAlertSetup();
  const relationshipQuery = getPatientRelationship();
</script>

<svelte:boundary>
  {#snippet pending()}
    <p class="text-sm text-muted-foreground">Loading…</p>
  {/snippet}
  {#snippet failed(error)}
    <p class="text-sm text-destructive">{remoteErrorMessage(error, "We couldn't load your alerts.")}</p>
  {/snippet}

  {@const status = await setupQuery}
  {@const voice = patientVoice(await relationshipQuery)}
  <AlertSetup {status} {voice} />
</svelte:boundary>
