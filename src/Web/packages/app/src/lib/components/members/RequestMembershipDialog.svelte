<script lang="ts">
  import * as Dialog from "#lib/components/ui/dialog/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import { FormError, FormField } from "#lib/forms/index.js";
  import Fingerprint from "@lucide/svelte/icons/fingerprint-pattern";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import {
    browserSupportsWebAuthn,
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import { browser } from "$app/env";
  import {
    accessRequestComplete,
    accessRequestOptions,
  } from "#lib/api/generated/passkeys.generated.remote.js";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "#lib/components/auth/passkey-errors.js";

  interface Props {
    open: boolean;
  }

  let { open = $bindable(false) }: Props = $props();

  const passkeysSupported = browser && browserSupportsWebAuthn();

  let displayName = $state("");
  let message = $state("");
  let sending = $state(false);
  let sent = $state(false);
  let error = $state<string | null>(null);

  const canSend = $derived(displayName.trim().length > 0 && !sending);

  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    if (!canSend) return;

    sending = true;
    error = null;
    const name = displayName.trim();

    try {
      const response = await accessRequestOptions({
        displayName: name,
        message: message.trim() || undefined,
      });
      const optionsJSON = parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
        response.options,
      );
      const attestation = await startRegistration({ optionsJSON });
      await accessRequestComplete({
        displayName: name,
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken: response.challengeToken ?? "",
      });
      sent = true;
    } catch (err) {
      console.error("Membership request failed:", err);
      error = describePasskeyError(err, "register", "We could not send your request. Try again.");
    } finally {
      sending = false;
    }
  }
</script>

<Dialog.Root bind:open>
  <Dialog.Content class="max-w-md" data-testid="request-membership-dialog">
    {#if sent}
      <Dialog.Header>
        <Dialog.Title>Request sent</Dialog.Title>
        <Dialog.Description>
          The owner must approve your request before you can sign in. After approval, sign in with
          the passkey you made.
        </Dialog.Description>
      </Dialog.Header>
      <Dialog.Footer>
        <Button onclick={() => (open = false)}>Close</Button>
      </Dialog.Footer>
    {:else if !passkeysSupported}
      <Dialog.Header>
        <Dialog.Title>Request membership</Dialog.Title>
        <Dialog.Description>
          This browser cannot make a passkey. To send a request, use a browser or device that
          supports passkeys.
        </Dialog.Description>
      </Dialog.Header>
      <Dialog.Footer>
        <Button variant="outline" onclick={() => (open = false)}>Close</Button>
      </Dialog.Footer>
    {:else}
      <Dialog.Header>
        <Dialog.Title>Request membership</Dialog.Title>
        <Dialog.Description>
          Tell the owner who you are. You make a passkey to send the request. The owner must
          approve the request before you can sign in.
        </Dialog.Description>
      </Dialog.Header>
      <form class="space-y-4" onsubmit={handleSubmit}>
        <FormField label="Your name" id="request-display-name" required>
          {#snippet control(field)}
            <Input
              {...field}
              name="displayName"
              type="text"
              autocomplete="name"
              maxlength={255}
              bind:value={displayName}
              disabled={sending}
            />
          {/snippet}
        </FormField>
        <FormField label="Message to the owner" id="request-message">
          {#snippet control(field)}
            <Textarea
              {...field}
              name="message"
              placeholder="e.g. I'm Sarah's endocrinologist"
              maxlength={500}
              rows={3}
              bind:value={message}
              disabled={sending}
            />
          {/snippet}
        </FormField>
        <p class="text-xs text-muted-foreground text-right">
          {message.length}/500
        </p>
        <FormError issues={error} focusOnShow />
        <Dialog.Footer>
          <Button type="button" variant="outline" onclick={() => (open = false)}>Cancel</Button>
          <Button type="submit" disabled={!canSend}>
            {#if sending}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              Waiting for passkey...
            {:else}
              <Fingerprint class="mr-2 h-4 w-4" />
              Send request
            {/if}
          </Button>
        </Dialog.Footer>
      </form>
    {/if}
  </Dialog.Content>
</Dialog.Root>
