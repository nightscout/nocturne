<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { FormError, FormField } from "$lib/forms";
  import {
    ShieldAlert,
    Fingerprint,
    Loader2,
    Check,
  } from "lucide-svelte";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import {
    recoveryModeOptions,
    recoveryModeComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "$lib/components/auth/passkey-errors";
  import { goto } from "$app/navigation";

  // Steps: identify -> codes -> done
  type Step = "identify" | "codes" | "done";
  let step = $state<Step>("identify");

  // Form state
  let username = $state("");
  let displayName = $state("");
  let isRegistering = $state(false);
  let errorMessage = $state<string | null>(null);
  let recoveryCodes = $state<string[]>([]);

  // The server resolves the account from the username and only proceeds when that account
  // has no passkey and no linked sign-in provider, so this can only restore access to an
  // account that is already locked out.

  /**
   * The completion response only carries recovery codes when the account had
   * none, so the field is absent from the generated response type.
   */
  function readRecoveryCodes(result: unknown): string[] {
    if (!result || typeof result !== "object" || !("recoveryCodes" in result)) {
      return [];
    }
    const { recoveryCodes: codes } = result;
    if (!Array.isArray(codes)) return [];
    return codes.filter((code): code is string => typeof code === "string");
  }

  /**
   * Registers a replacement passkey for the account named in the form. The
   * WebAuthn ceremony runs in the browser, so this step needs JavaScript; the
   * form is here for Enter-to-submit, required-field checks and autofill.
   */
  async function handleRegister(event: SubmitEvent) {
    event.preventDefault();
    if (isRegistering || !username.trim()) return;

    isRegistering = true;
    errorMessage = null;

    try {
      const response = await recoveryModeOptions({
        username: username.trim(),
      });
      const options = parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
        response.options
      );
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      const result = await recoveryModeComplete({
        username: username.trim(),
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken,
        label: `${displayName.trim() || username.trim()}'s passkey`,
      });

      recoveryCodes = readRecoveryCodes(result);

      step = recoveryCodes.length > 0 ? "codes" : "done";
    } catch (err) {
      console.error("Recovery-mode passkey registration failed:", err);
      errorMessage = describePasskeyError(
        err,
        "register",
        "We couldn't register a passkey for that username. This page only works for an account that has no passkey and no linked sign-in provider — otherwise sign in with a recovery code."
      );
    } finally {
      isRegistering = false;
    }
  }

  function handleContinue() {
    goto("/", { replaceState: true });
  }
</script>

<svelte:head>
  <title>Recovery Mode - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <div class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-amber-500/10">
        <ShieldAlert class="h-6 w-6 text-amber-500" />
      </div>
      <Card.Title class="text-xl">Recovery Mode</Card.Title>
      <Card.Description>
        This instance has accounts that need a passkey registered. Enter your username and register a passkey to restore access.
      </Card.Description>
    </Card.Header>

    <Card.Content>
      {#if step === "identify"}
        <form class="space-y-4" onsubmit={handleRegister}>
          <FormField label="Username" id="recovery-username" required>
            {#snippet control(field)}
              <Input
                {...field}
                name="username"
                type="text"
                placeholder="your-username"
                autocomplete="username"
                autocapitalize="none"
                spellcheck={false}
                autofocus
                bind:value={username}
                disabled={isRegistering}
              />
            {/snippet}
          </FormField>

          <FormField
            label="Display name"
            id="recovery-display-name"
            description="Optional. Updates your display name if provided."
          >
            {#snippet control(field)}
              <Input
                {...field}
                name="displayName"
                type="text"
                placeholder="Your name"
                autocomplete="name"
                bind:value={displayName}
                disabled={isRegistering}
              />
            {/snippet}
          </FormField>

          <FormError issues={errorMessage} focusOnShow />

          <Button
            type="submit"
            class="w-full"
            size="lg"
            disabled={!username.trim() || isRegistering}
          >
            {#if isRegistering}
              <Loader2 class="mr-2 h-5 w-5 animate-spin" />
              Waiting for passkey...
            {:else}
              <Fingerprint class="mr-2 h-5 w-5" />
              Register passkey
            {/if}
          </Button>
        </form>
      {:else if step === "codes"}
        <div class="space-y-4">
          <div class="flex items-start gap-3 rounded-md border border-green-500/20 bg-green-500/5 p-3">
            <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600" />
            <p class="text-sm text-green-700 dark:text-green-400">
              Passkey registered successfully.
            </p>
          </div>

          <RecoveryCodes codes={recoveryCodes} onContinue={handleContinue} />
        </div>
      {:else if step === "done"}
        <div class="space-y-4">
          <div class="flex items-start gap-3 rounded-md border border-green-500/20 bg-green-500/5 p-3">
            <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600" />
            <p class="text-sm text-green-700 dark:text-green-400">
              Passkey registered successfully. Recovery mode has been deactivated.
            </p>
          </div>
          <Button class="w-full" onclick={handleContinue}>
            Continue
          </Button>
        </div>
      {/if}
    </Card.Content>

    <Card.Footer class="justify-center">
      <p class="text-xs text-muted-foreground">
        Register a passkey to restore normal access.
      </p>
    </Card.Footer>
  </Card.Root>
</div>
