<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import Mail from "@lucide/svelte/icons/mail";
  import { goto } from "$app/navigation";
  import { Button } from "#lib/components/ui/button/index.js";
  import RecoveryEmailForm from "#lib/components/account/RecoveryEmailForm.svelte";
  import type { RecoveryEmailOutcome } from "#lib/components/account/recovery-email-outcome.js";
  import RecoveryEmailOutcomeNotice from "#lib/components/account/RecoveryEmailOutcomeNotice.svelte";
  import { updateStatus } from "#lib/api/generated/coachMarks.generated.remote.js";
  import type { PageData } from "./$types";

  // The same mark the reminder uses, so a skip is asked again after the reminder's own delay.
  const MARK_KEY = "account.recovery-email";

  let { data }: { data: PageData } = $props();

  let outcome = $state<RecoveryEmailOutcome | null>(null);

  function saved(result: RecoveryEmailOutcome) {
    if (result === "sent" || result === "unchanged") return leave();
    outcome = result;
  }

  function leave() {
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the server load reduced next to a same-origin path
    return goto(data.next, { replace: true });
  }

  async function skip() {
    try {
      await updateStatus({ key: MARK_KEY, request: { status: "dismissed" } });
    } catch (err) {
      console.error("Failed to record the skipped recovery email step:", err);
    }
    await leave();
  }
</script>

<svelte:head>
  <title>Recovery email - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="space-y-1 text-center">
      <div class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10">
        <Mail class="h-6 w-6 text-primary" />
      </div>
      <Card.Title class="text-2xl font-bold">Add a recovery email</Card.Title>
      <Card.Description>
        If you lose this device, we send a link to your recovery email so you can get back in. You can skip this and add it later in account settings.
      </Card.Description>
    </Card.Header>
    <Card.Content>
      {#if outcome}
        <div class="space-y-3">
          <RecoveryEmailOutcomeNotice {outcome} canResend={false} class="text-sm" />
          <Button class="w-full" onclick={leave}>Continue</Button>
        </div>
      {:else}
        <RecoveryEmailForm
          submitLabel="Save and send confirmation"
          onSaved={saved}
          onSkip={skip}
        />
      {/if}
    </Card.Content>
  </Card.Root>
</div>
