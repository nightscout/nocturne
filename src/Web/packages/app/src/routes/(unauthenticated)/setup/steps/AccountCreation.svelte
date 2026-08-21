<script lang="ts">
  import { Check, Fingerprint, Loader2, UserPlus } from "lucide-svelte";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import {
    getAuthState,
    getOidcProviders,
    setAuthCookies,
  } from "$routes/(unauthenticated)/auth/auth.remote";
  import {
    setupOwnerOptions,
    setupOwnerComplete,
    setupOwnerOidc,
    validateSetupUsername,
  } from "../setup.remote";
  import { FormError, FormField, useAvailability } from "$lib/forms";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "$lib/components/auth/passkey-errors";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import OidcProviderButtons from "$lib/components/auth/OidcProviderButtons.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";

  let {
    onComplete,
  }: {
    onComplete: () => void;
  } = $props();

  // ── Remote data ───────────────────────────────────────────────────
  const authStateQuery = getAuthState();
  const oidcQuery = getOidcProviders();

  const isAuthenticated = $derived(
    authStateQuery.current?.isAuthenticated ?? false,
  );
  const oidc = $derived(oidcQuery.current);
  const hasOidc = $derived(oidc?.enabled && (oidc?.providers?.length ?? 0) > 0);

  // ── Auto-advance if already authenticated ─────────────────────────
  $effect(() => {
    if (isAuthenticated && !registrationComplete) {
      onComplete();
    }
  });

  // ── Shared form fields ───────────────────────────────────────────
  let displayName = $state("");
  let username = $state("");

  // ── Username validation ─────────────────────────────────────────────
  const normalizedUsername = $derived(username.trim().toLowerCase());

  const availability = useAvailability(
    () => normalizedUsername,
    (value) => validateSetupUsername({ username: value }),
    { label: "Username" },
  );

  const canSubmit = $derived(
    displayName.trim().length > 0 && availability.submittable,
  );

  // ── OIDC login ───────────────────────────────────────────────────
  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);
  let oidcError = $state<string | null>(null);

  async function loginWithProvider(providerId: string) {
    if (!canSubmit) return;
    isRedirecting = true;
    selectedProvider = providerId;
    oidcError = null;

    try {
      const result = await setupOwnerOidc({
        username: username.trim().toLowerCase(),
        displayName: displayName.trim(),
        providerId,
      });
      window.location.href = result.authorizationUrl ?? "/setup";
    } catch (err) {
      console.error("Starting the external sign-in failed:", err);
      oidcError =
        "We couldn't start sign-in with that provider. Please try again.";
      isRedirecting = false;
      selectedProvider = null;
    }
  }

  // ── Passkey registration ─────────────────────────────────────────
  let isRegistering = $state(false);
  let registrationComplete = $state(false);
  let recoveryCodes = $state<string[]>([]);
  let passkeyError = $state<string | null>(null);

  /**
   * Creates the owner account. The WebAuthn ceremony runs in the browser, so
   * this step needs JavaScript; the form gives it Enter-to-submit, the
   * browser's required-field checks and autofill.
   */
  async function handlePasskeyRegister(event: SubmitEvent) {
    event.preventDefault();
    if (!canSubmit || isRedirecting || isRegistering) return;
    isRegistering = true;
    passkeyError = null;

    try {
      const response = await setupOwnerOptions({
        username: username.trim().toLowerCase(),
        displayName: displayName.trim(),
      });
      const options = parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
        response.options
      );
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      const result = await setupOwnerComplete({
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
      console.error("Owner passkey registration failed:", err);
      passkeyError = describePasskeyError(
        err,
        "register",
        "We couldn't create your account. Please try again."
      );
    } finally {
      isRegistering = false;
    }
  }

  // ── Combined error display ───────────────────────────────────────
  const errorMessage = $derived(passkeyError ?? oidcError);
</script>

<div class="flex flex-col items-center gap-10 px-4 py-8">
  <!-- Heading -->
  <div class="flex flex-col items-center gap-4 text-center">
    <h1
      class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
      style="font-size: clamp(32px, 4vw, 48px);"
    >
      Create your <em class="not-italic font-light" style="color: var(--onb-teal);">account</em>.
    </h1>
    <p class="max-w-140 text-base leading-relaxed text-white/50">
      Set up the owner account for your Nocturne instance. You will be the
      administrator.
    </p>
  </div>

  <!-- Form area -->
  <div class="w-full max-w-md">
    {#if registrationComplete}
      <div class="space-y-4">
        <div class="flex flex-col items-center gap-2 text-center">
          <div
            class="flex h-12 w-12 items-center justify-center rounded-full"
            style="background: var(--onb-ok); color: var(--onb-navy);"
          >
            <UserPlus class="h-6 w-6" />
          </div>
          <h2 class="text-lg font-semibold text-white">Account Created</h2>
          <p class="text-sm text-white/50">
            Save your recovery codes before continuing.
          </p>
        </div>

        <RecoveryCodes
          codes={recoveryCodes}
          onContinue={onComplete}
          continueLabel="Continue Setup"
        />
      </div>
    {:else if authStateQuery.loading}
      <div class="flex items-center justify-center py-12">
        <Loader2 class="h-8 w-8 animate-spin text-white/40" />
      </div>
    {:else if !isAuthenticated}
      <form class="space-y-4" onsubmit={handlePasskeyRegister}>
        <FormError issues={errorMessage} focusOnShow />

        <!-- Shared form fields -->
        <FormField
          label="Display name"
          id="display-name"
          required
          labelClass="text-white/70"
          description="This is how you will appear to others."
        >
          {#snippet control(field)}
            <Input
              {...field}
              name="displayName"
              type="text"
              placeholder="Your name"
              autocomplete="name"
              autofocus
              bind:value={displayName}
              disabled={isRedirecting || isRegistering}
              class="bg-white/5 border-white/10 text-white placeholder:text-white/25"
            />
          {/snippet}
        </FormField>

        <FormField
          label="Username"
          id="pk-username"
          required
          labelClass="text-white/70"
          issues={availability.error}
        >
          {#snippet control(field)}
            <Input
              {...field}
              name="username"
              type="text"
              placeholder="your-username"
              autocomplete="username"
              autocapitalize="none"
              spellcheck={false}
              minlength={3}
              bind:value={username}
              disabled={isRedirecting || isRegistering}
              class="bg-white/5 border-white/10 text-white placeholder:text-white/25 {availability.error
                ? 'border-red-500/50'
                : availability.valid
                  ? 'border-green-500/50'
                  : ''}"
            />
          {/snippet}
          {#snippet hint()}
            {#if availability.validating}
              <p class="text-xs text-white/40">Checking availability...</p>
            {:else if availability.valid}
              <p class="flex items-center gap-1.5 text-xs text-green-400">
                <Check class="h-3 w-3" />
                Available
              </p>
            {:else}
              <p class="text-xs text-white/30">
                3-32 characters: letters, numbers, dots, underscores, and hyphens.
              </p>
            {/if}
          {/snippet}
        </FormField>

        <!-- Auth method buttons -->
        {#if hasOidc && oidc}
          <OidcProviderButtons
            providers={oidc.providers}
            disabled={!canSubmit || isRedirecting || isRegistering}
            onLogin={loginWithProvider}
            {isRedirecting}
            {selectedProvider}
          />
        {/if}

        <Button
          type="submit"
          class="w-full"
          size="lg"
          disabled={!canSubmit || isRedirecting || isRegistering}
        >
          {#if isRegistering}
            <Loader2 class="mr-2 h-5 w-5 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-5 w-5" />
            Create account with passkey
          {/if}
        </Button>
      </form>
    {/if}
  </div>
</div>
