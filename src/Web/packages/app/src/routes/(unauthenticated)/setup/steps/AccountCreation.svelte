<script lang="ts">
  import { AlertTriangle, Check, Fingerprint, Loader2, UserPlus } from "lucide-svelte";
  import { startRegistration } from "@simplewebauthn/browser";
  import {
    setupOptions,
    setupComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import {
    getAuthState,
    getOidcProviders,
    setAuthCookies,
  } from "$routes/(unauthenticated)/auth/auth.remote";
  import { setupOwnerOidc, validateSetupUsername } from "../setup.remote";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import OidcProviderButtons from "$lib/components/auth/OidcProviderButtons.svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";

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
  let usernameError = $state<string | null>(null);
  let usernameValid = $state(false);
  let validatingUsername = $state(false);
  let validationTimeout: ReturnType<typeof setTimeout> | null = null;

  function onUsernameInput() {
    usernameError = null;
    usernameValid = false;

    if (validationTimeout) clearTimeout(validationTimeout);

    const value = username.trim().toLowerCase();
    if (!value || value.length < 3) {
      if (value.length > 0) usernameError = "Username must be at least 3 characters";
      return;
    }

    validatingUsername = true;
    validationTimeout = setTimeout(async () => {
      try {
        const result = await validateSetupUsername({ username: value });
        if (result?.isValid) {
          usernameValid = true;
          usernameError = null;
        } else {
          usernameValid = false;
          usernameError = result?.message ?? "Invalid username";
        }
      } catch {
        usernameError = "Could not validate username";
      } finally {
        validatingUsername = false;
      }
    }, 400);
  }

  const canSubmit = $derived(
    displayName.trim().length > 0 && usernameValid,
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
      oidcError = err instanceof Error ? err.message : "Failed to start OIDC login.";
      isRedirecting = false;
      selectedProvider = null;
    }
  }

  // ── Passkey registration ─────────────────────────────────────────
  let isRegistering = $state(false);
  let registrationComplete = $state(false);
  let recoveryCodes = $state<string[]>([]);
  let passkeyError = $state<string | null>(null);

  async function handlePasskeyRegister() {
    if (!canSubmit) return;
    isRegistering = true;
    passkeyError = null;

    try {
      const response = await setupOptions({
        username: username.trim().toLowerCase(),
        displayName: displayName.trim(),
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
      <div class="space-y-4">
        {#if errorMessage}
          <div
            class="flex items-start gap-3 rounded-lg border border-red-500/20 bg-red-500/5 p-4"
          >
            <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-red-400" />
            <p class="text-sm text-red-400">{errorMessage}</p>
          </div>
        {/if}

        <!-- Shared form fields -->
        <div class="space-y-2">
          <Label for="display-name">Display name</Label>
          <Input
            id="display-name"
            type="text"
            placeholder="Your name"
            bind:value={displayName}
            disabled={isRedirecting || isRegistering}
          />
          <p class="text-xs text-muted-foreground">
            This is how you will appear to others.
          </p>
        </div>

        <div class="space-y-2">
          <Label for="pk-username">Username</Label>
          <Input
            id="pk-username"
            type="text"
            placeholder="your-username"
            bind:value={username}
            oninput={onUsernameInput}
            disabled={isRedirecting || isRegistering}
          />
          {#if validatingUsername}
            <p class="text-xs text-white/40">Checking availability...</p>
          {:else if usernameError}
            <p class="text-xs text-red-400">{usernameError}</p>
          {:else if usernameValid}
            <p class="flex items-center gap-1.5 text-xs text-green-400">
              <Check class="h-3 w-3" />
              Available
            </p>
          {:else}
            <p class="text-xs text-muted-foreground">
              3-32 characters: letters, numbers, dots, underscores, and hyphens.
            </p>
          {/if}
        </div>

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
          class="w-full"
          size="lg"
          disabled={!canSubmit || isRedirecting || isRegistering}
          onclick={handlePasskeyRegister}
        >
          {#if isRegistering}
            <Loader2 class="mr-2 h-5 w-5 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-5 w-5" />
            Create account with passkey
          {/if}
        </Button>
      </div>
    {/if}
  </div>
</div>
