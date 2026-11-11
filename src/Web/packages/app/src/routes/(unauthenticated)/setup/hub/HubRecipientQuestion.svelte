<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import * as Card from "#lib/components/ui/card/index.js";
  import {
    getPatientRelationship,
    setPatientRelationship,
  } from "#lib/api/generated/tenantSettings.generated.remote.js";
  import { PatientRelationship } from "#lib/api/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import WhoFor from "../steps/WhoFor.svelte";

  let { offered }: { offered: PatientRelationship[] } = $props();

  const relationshipQuery = getPatientRelationship();

  let relationship = $state<PatientRelationship | undefined>(undefined);
  /** Starts from the name the helper gave, until the recipient types their own. */
  let typedName = $state<string | undefined>(undefined);
  const patientName = $derived(typedName ?? relationshipQuery.current?.patientName ?? "");
  let error = $state<string | undefined>(undefined);
  let saving = $state(false);

  async function save() {
    if (!relationship) return;
    saving = true;
    error = undefined;
    try {
      await setPatientRelationship({
        relationship,
        patientName: patientName.trim() || undefined,
      });
    } catch (err) {
      error = describeSubmitError(err, "We could not save your answer.");
    } finally {
      saving = false;
    }
  }
</script>

<Card.Root size="flush" data-testid="hub-recipient-question">
  <WhoFor
    bind:relationship
    bind:patientName={() => patientName, (value) => (typedName = value)}
    {offered}
    {error}
  />
  <div class="flex justify-end border-t px-6 py-4">
    <Button onclick={save} disabled={!relationship || saving}>Save</Button>
  </div>
</Card.Root>
