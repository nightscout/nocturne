<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    ShieldAlert,
    Fingerprint,
    Loader2,
    AlertTriangle,
    Check,
  } from "lucide-svelte";
  import { startRegistration } from "@simplewebauthn/browser";
  import {
    registerOptions,
    registerComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import { goto } from "$app/navigation";

  // Steps: identify -> register -> codes -> done
  type Step = "identify" | "register" | "codes" | "done";
  let step = $state<Step>("identify");

  // Form state
  let username = $state("");
  let displayName = $state("");
  let isRegistering = $state(false);
  let errorMessage = $state<string | null>(null);
  let recoveryCodes = $state<string[]>([]);

  // In recovery mode, we need to find the orphaned subject.
  // The register/options endpoint will look up the subject by username.
  // For now, we collect username + display name and attempt registration.

  async function handleRegister() {
    if (!username.trim()) {
      errorMessage = "Username is required.";
      return;
    }

    isRegistering = true;
    errorMessage = null;

    try {
      // registerOptions will find the subject by username
      const response = await registerOptions({
        username: username.trim(),
      });
      const options = JSON.parse(response.options ?? "");
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      const result = await registerComplete({
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken,
        label: `${displayName.trim() || username.trim()}'s passkey`,
      });

      // Check if recovery codes were returned
      if (result && "recoveryCodes" in result) {
        recoveryCodes = (result as any).recoveryCodes ?? [];
      }

      step = recoveryCodes.length > 0 ? "codes" : "done";
    } catch (err) {
      errorMessage =
        err instanceof Error ? err.message : "Failed to register passkey. Check that your username is correct.";
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
      {#if step === "identify" || step === "register"}
        <div class="space-y-4">
          <div class="space-y-2">
            <Label for="recovery-username">Username</Label>
            <Input
              id="recovery-username"
              type="text"
              placeholder="your-username"
              bind:value={username}
              disabled={isRegistering}
            />
          </div>

          <div class="space-y-2">
            <Label for="recovery-display-name">Display name</Label>
            <Input
              id="recovery-display-name"
              type="text"
              placeholder="Your name"
              bind:value={displayName}
              disabled={isRegistering}
            />
            <p class="text-xs text-muted-foreground">
              Optional. Updates your display name if provided.
            </p>
          </div>

          {#if errorMessage}
            <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
              <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
              <p class="text-sm text-destructive">{errorMessage}</p>
            </div>
          {/if}

          <Button
            class="w-full"
            size="lg"
            disabled={!username.trim() || isRegistering}
            onclick={handleRegister}
          >
            {#if isRegistering}
              <Loader2 class="mr-2 h-5 w-5 animate-spin" />
              Waiting for passkey...
            {:else}
              <Fingerprint class="mr-2 h-5 w-5" />
              Register passkey
            {/if}
          </Button>
        </div>
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
