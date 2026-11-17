<script lang="ts">
  import { getAlertSetup } from "#lib/api/generated/setupAlerts.generated.remote.js";
  import { getPatientRelationship } from "#lib/api/generated/tenantSettings.generated.remote.js";
  import { remoteErrorMessage } from "#lib/api/remote-error.js";
  import { patientVoice } from "#lib/onboarding/patient-voice.svelte.js";
  import AlertSetup from "./AlertSetup.svelte";

  const setupQuery = getAlertSetup();
  const relationshipQuery = getPatientRelationship();
</script>

<svelte:boundary>
  {#snippet pending()}
    <p class="text-sm text-muted-foreground">Loading…</p>
  {/snippet}
  {#snippet failed(error)}
    <p class="text-sm text-destructive">{remoteErrorMessage(error, "We could not load your alerts.")}</p>
  {/snippet}

  {@const status = await setupQuery}
  {@const voice = patientVoice(await relationshipQuery)}
  <AlertSetup {status} {voice} />
</svelte:boundary>
