<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { FormError } from "$lib/forms";
  import { Fingerprint, KeyRound, Loader2, Check } from "lucide-svelte";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import {
    registerOptions,
    registerComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "$lib/components/auth/passkey-errors";
  import { page } from "$app/state";

  /**
   * Where a spent recovery code lands. The account is named by the recovery session the
   * verify step set, so the username here is only what the new credential is labelled
   * with; naming someone else changes nothing about which account is enrolled.
   */
  const username = $derived(page.url.searchParams.get("username") ?? "");
  const returnUrl = $derived(page.url.searchParams.get("returnUrl") ?? "/");
  const loginUrl = $derived(
    `/auth/login?${new URLSearchParams({ returnUrl })}`
  );

  let isRegistering = $state(false);
  let registered = $state(false);
  let errorMessage = $state<string | null>(null);

  /**
   * Enrols a replacement passkey against the recovery session. The WebAuthn ceremony runs
   * in the browser, so this step needs JavaScript.
   */
  async function handleRegister() {
    if (isRegistering) return;
    isRegistering = true;
    errorMessage = null;

    try {
      const response = await registerOptions({ username });
      const options =
        parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
          response.options
        );

      const attestation = await startRegistration({ optionsJSON: options });

      await registerComplete({
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken: response.challengeToken ?? "",
        label: username ? `${username}'s passkey` : undefined,
      });

      registered = true;
    } catch (err) {
      console.error("Recovery passkey registration failed:", err);
      errorMessage = describePasskeyError(
        err,
        "register",
        "We couldn't register a passkey. A recovery code is only good for ten minutes — sign in with another one to try again."
      );
    } finally {
      isRegistering = false;
    }
  }
</script>

<svelte:head>
  <title>Register a Passkey - Nocturne</title>
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <div
        class="mx-auto mb-2 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
      >
        <KeyRound class="h-6 w-6 text-primary" />
      </div>
      <Card.Title class="text-xl">Register a passkey</Card.Title>
      <Card.Description>
        {#if registered}
          Your passkey is ready. Sign in with it to finish.
        {:else}
          Your recovery code was accepted. It lets you set up a passkey for
          {username || "your account"} — nothing else — and it expires in ten
          minutes.
        {/if}
      </Card.Description>
    </Card.Header>

    <Card.Content class="space-y-4">
      {#if registered}
        <div
          class="flex items-start gap-3 rounded-md border border-green-500/20 bg-green-500/5 p-3"
        >
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-green-600" />
          <p class="text-sm text-green-700 dark:text-green-400">
            Passkey registered. Your recovery code is now used up.
          </p>
        </div>

        <Button class="w-full" size="lg" href={loginUrl}>
          Sign in with your passkey
        </Button>
      {:else}
        <FormError issues={errorMessage} focusOnShow />

        <Button
          class="w-full"
          size="lg"
          onclick={handleRegister}
          disabled={isRegistering}
        >
          {#if isRegistering}
            <Loader2 class="mr-2 h-5 w-5 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-5 w-5" />
            Register passkey
          {/if}
        </Button>
      {/if}
    </Card.Content>

    <Card.Footer class="justify-center">
      <a href={loginUrl} class="text-xs text-muted-foreground hover:underline">
        Back to sign in
      </a>
    </Card.Footer>
  </Card.Root>
</div>
