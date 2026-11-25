<script lang="ts">
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import Mail from "@lucide/svelte/icons/mail";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import { FormError } from "#lib/forms/index.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import RecoveryEmailForm from "#lib/components/account/RecoveryEmailForm.svelte";
  import {
    getRecoveryEmail,
    deleteRecoveryEmail,
  } from "#lib/api/generated/recoveryEmails.generated.remote.js";
  import type { RecoveryEmailOutcome } from "#lib/components/account/recovery-email-outcome.js";
  import RecoveryEmailOutcomeNotice from "#lib/components/account/RecoveryEmailOutcomeNotice.svelte";
  import { send } from "#lib/api/generated/emailConfirmations.generated.remote.js";

  const query = getRecoveryEmail();
  const info = $derived(query.current);

  let editing = $state(false);
  let busy = $state<"resend" | "remove" | null>(null);
  let notice = $state<RecoveryEmailOutcome | null>(null);
  let error = $state<string | null>(null);

  function saved(outcome: RecoveryEmailOutcome) {
    editing = false;
    error = null;
    notice = outcome;
  }

  async function resend() {
    busy = "resend";
    error = null;
    notice = null;
    try {
      await send();
      notice = "sent";
    } catch (err) {
      error = describeSubmitError(err, "We could not send the link. Try again in a few minutes.", {
        email_link_throttled: "We sent a link a short time ago. Wait a few minutes, then try again.",
        email_delivery_failed: "We could not send the link. Try again in a few minutes.",
      });
    } finally {
      busy = null;
    }
  }

  async function remove() {
    busy = "remove";
    error = null;
    notice = null;
    try {
      await deleteRecoveryEmail();
    } catch (err) {
      error = describeSubmitError(err, "We could not remove your recovery email. Try again.");
    } finally {
      busy = null;
    }
  }
</script>

{#if info?.available}
  <Card.Root id="recovery-email">
    <Card.Header>
      <Card.Title class="flex items-center gap-2">
        <Mail class="h-5 w-5" />
        Recovery email
      </Card.Title>
      <Card.Description>
        If you lose your sign-in, we send a link to this address so you can get back in. Use an address that only you can read.
      </Card.Description>
    </Card.Header>
    <Card.Content class="space-y-3">
      {#if !info.email || editing}
        <RecoveryEmailForm
          submitLabel="Save and send confirmation"
          initialEmail={editing ? (info.email ?? "") : ""}
          onSaved={saved}
          onCancel={editing ? () => (editing = false) : undefined}
        />
      {:else}
        <div class="flex flex-wrap items-center justify-between gap-2">
          <p class="text-sm font-medium break-all">{info.email}</p>
          {#if info.confirmed}
            <Badge variant="secondary">Confirmed</Badge>
          {:else}
            <Badge variant="destructive">Not confirmed yet</Badge>
          {/if}
        </div>
        <div class="flex flex-wrap gap-2">
          {#if !info.confirmed}
            <Button variant="outline" disabled={busy !== null} onclick={resend}>
              {#if busy === "resend"}
                <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
              {/if}
              Resend
            </Button>
          {/if}
          <Button variant="outline" disabled={busy !== null} onclick={() => (editing = true)}>Change</Button>
          <Button variant="outline" disabled={busy !== null} onclick={remove}>
            {#if busy === "remove"}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {/if}
            Remove
          </Button>
        </div>
      {/if}
      {#if notice}
        <RecoveryEmailOutcomeNotice outcome={notice} canResend class="text-sm text-muted-foreground" />
      {/if}
      <FormError issues={error} />
    </Card.Content>
  </Card.Root>
{/if}
