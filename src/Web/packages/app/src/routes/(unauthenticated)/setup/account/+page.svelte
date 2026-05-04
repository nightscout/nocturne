<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { AlertTriangle, Loader2, UserPlus } from "lucide-svelte";
  import { startRegistration } from "@simplewebauthn/browser";
  import { goto } from "$app/navigation";
  import {
    setupOptions,
    setupComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import {
    getAuthState,
    getOidcProviders,
    setAuthCookies,
  } from "$routes/(unauthenticated)/auth/auth.remote";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import OidcProviderButtons from "$lib/components/auth/OidcProviderButtons.svelte";
  import PasskeyRegistrationForm from "$lib/components/auth/PasskeyRegistrationForm.svelte";

  // ── Remote data ───────────────────────────────────────────────────
  const authStateQuery = getAuthState();
  const oidcQuery = getOidcProviders();

  const isAuthenticated = $derived(
    authStateQuery.current?.isAuthenticated ?? false,
  );
  const oidc = $derived(oidcQuery.current);
  const hasOidc = $derived(oidc?.enabled && (oidc?.providers?.length ?? 0) > 0);

  // ── Redirect if already authenticated ─────────────────────────────
  // Guard with !registrationComplete so the $effect doesn't fire after
  // setAuthCookies auto-invalidates queries during the passkey flow —
  // the user needs to see their recovery codes before navigating away.
  $effect(() => {
    if (isAuthenticated && !registrationComplete) {
      goto("/setup", { replaceState: true, invalidateAll: true });
    }
  });

  // ── OIDC login ───────────────────────────────────────────────────
  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);

  function loginWithProvider(providerId: string) {
    isRedirecting = true;
    selectedProvider = providerId;
    const params = new URLSearchParams();
    params.set("provider", providerId);
    params.set("returnUrl", "/setup/account");
    window.location.href = `/api/auth/oidc/login?${params.toString()}`;
  }

  // ── Passkey registration ─────────────────────────────────────────
  let isRegistering = $state(false);
  let registrationComplete = $state(false);
  let recoveryCodes = $state<string[]>([]);
  let passkeyError = $state<string | null>(null);

  async function handlePasskeyRegister(
    username: string,
    displayName: string,
  ) {
    isRegistering = true;
    passkeyError = null;

    try {
      const response = await setupOptions({
        username,
        displayName,
      });
      const options = JSON.parse(response.options ?? "");
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      const result = await setupComplete({
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken,
      });

      if (result.accessToken) {
        await setAuthCookies({
          accessToken: result.accessToken,
          refreshToken: result.refreshToken ?? undefined,
          expiresIn: result.expiresIn ?? undefined,
        });
      }

      registrationComplete = true;
      recoveryCodes = result.recoveryCodes ?? [];
    } catch (err) {
      passkeyError =
        err instanceof Error ? err.message : "Failed to create account.";
    } finally {
      isRegistering = false;
    }
  }

  function handleContinueToSetup() {
    goto("/setup", { replaceState: true, invalidateAll: true });
  }
</script>

<svelte:head>
  <title>Create Account - Setup - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    {#if authStateQuery.loading}
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Loader2 class="h-6 w-6 text-primary animate-spin" />
        </div>
        <Card.Title class="text-2xl font-bold">Loading...</Card.Title>
      </Card.Header>
    {:else if registrationComplete}
      <Card.Header class="space-y-1 text-center">
        <Card.Title class="text-2xl font-bold">Account Created</Card.Title>
        <Card.Description>
          Your admin account has been created. Save your recovery codes before
          continuing.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <RecoveryCodes
          codes={recoveryCodes}
          onContinue={handleContinueToSetup}
          continueLabel="Continue Setup"
        />
      </Card.Content>
    {:else if !isAuthenticated}
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <UserPlus class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Create Your Account</Card.Title>
        <Card.Description>
          Set up the admin account for your Nocturne instance.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <div class="space-y-4">
          {#if passkeyError}
            <div
              class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
            >
              <AlertTriangle
                class="mt-0.5 h-4 w-4 shrink-0 text-destructive"
              />
              <p class="text-sm text-destructive">{passkeyError}</p>
            </div>
          {/if}

          {#if hasOidc && oidc}
            <OidcProviderButtons
              providers={oidc.providers}
              disabled={isRedirecting || isRegistering}
              onLogin={loginWithProvider}
              {isRedirecting}
              {selectedProvider}
            />
          {/if}

          <PasskeyRegistrationForm
            onRegister={handlePasskeyRegister}
            disabled={isRedirecting}
            {isRegistering}
          />
        </div>
      </Card.Content>
    {/if}
  </Card.Root>
</div>
