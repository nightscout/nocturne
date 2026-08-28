<script lang="ts">
  import type { ComponentProps } from "svelte";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Loader2,
    ExternalLink,
    Fingerprint,
    User,
    KeyRound,
    Smartphone,
    ShieldAlert,
  } from "lucide-svelte";
  import * as InputOTP from "$lib/components/ui/input-otp";
  import { FormError, FormField, useSubmission } from "$lib/forms";
  import {
    startAuthentication,
    type PublicKeyCredentialRequestOptionsJSON,
  } from "@simplewebauthn/browser";
  import {
    getOidcProviders,
    setAuthCookies,
    signInWithAuthenticator,
    signInWithRecoveryCode,
  } from "$routes/(unauthenticated)/auth/auth.remote";
  import {
    discoverableLoginOptions,
    loginOptions,
    loginComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import { goto, invalidateAll } from "$app/navigation";
  import { describePasskeyError, parseCeremonyOptions } from "./passkey-errors";
  import { signInMethodLabels } from "./labels";

  interface Props {
    returnUrl?: string;
    onSuccess?: () => void;
    /**
     * Whether this page is served on a host that resolves no tenant. Passkeys, authenticator
     * codes, and recovery codes are all checked against a resolved tenant's members, so on such
     * a host only the identity-provider path can complete a sign-in and it is the only one
     * offered.
     */
    tenantless?: boolean;
  }

  let { returnUrl = "/", onSuccess, tenantless = false }: Props = $props();

  const oidcQuery = getOidcProviders();

  // UI mode
  type LoginMode = "default" | "username" | "recovery" | "totp";
  let mode = $state<LoginMode>("default");
  let isLoading = $state(false);
  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);

  /**
   * Errors from the two passkey ceremonies. The code-based forms carry their own
   * errors on the remote form's fields.
   */
  let passkeyError = $state<string | null>(null);

  const recovery = useSubmission({
    fallback: "We couldn't sign you in just now. Please try again.",
  });
  const authenticator = useSubmission({
    fallback: "We couldn't sign you in just now. Please try again.",
  });

  // Browser support
  let passkeysSupported = $state(
    typeof window !== "undefined" && window.PublicKeyCredential !== undefined
  );

  // Form fields
  let username = $state("");
  let totpCode = $state("");
  /** Submitted from the code field's onComplete, so a full code submits itself. */
  let totpFormEl = $state<HTMLFormElement | null>(null);
  /**
   * Proof from the API that the passkey step succeeded. The authenticator code is
   * a second factor, so it is only accepted alongside this token.
   */
  let stepUpToken = $state("");

  async function handleAuthResult(result: {
    success?: boolean;
    accessToken?: string;
    refreshToken?: string;
    expiresIn?: number;
    refreshExpiresIn?: number;
    totpRequired?: boolean;
    stepUpToken?: string | null;
    error?: string;
  }) {
    if (!result.success) {
      passkeyError =
        result.error ?? "We couldn't sign you in. Please try again.";
      return;
    }

    // The passkey was accepted but this account also has an authenticator, so there is
    // no session yet — collect the code and finish there.
    if (result.totpRequired) {
      if (!result.stepUpToken) {
        passkeyError = "We couldn't sign you in. Please try again.";
        return;
      }
      stepUpToken = result.stepUpToken;
      totpCode = "";
      authenticator.clear();
      mode = "totp";
      return;
    }

    // Set auth cookies via server-side command
    if (result.accessToken) {
      await setAuthCookies({
        accessToken: result.accessToken,
        refreshToken: result.refreshToken,
        expiresIn: result.expiresIn,
        refreshExpiresIn: result.refreshExpiresIn,
      });
    }

    await invalidateAll();

    if (onSuccess) {
      onSuccess();
    } else {
      await goto(returnUrl, { invalidateAll: true });
    }
  }

  /**
   * Discoverable ("just tap the button") passkey sign-in. The WebAuthn ceremony
   * runs in the browser, so this path can't work without JavaScript.
   */
  async function handleDiscoverableLogin(event: SubmitEvent) {
    event.preventDefault();
    isLoading = true;
    passkeyError = null;

    try {
      const response = await discoverableLoginOptions();
      const options = parseCeremonyOptions<PublicKeyCredentialRequestOptionsJSON>(
        response.options
      );
      const challengeToken = response.challengeToken ?? "";

      const assertion = await startAuthentication({ optionsJSON: options });

      const result = await loginComplete({
        assertionResponseJson: JSON.stringify(assertion),
        challengeToken,
      });
      await handleAuthResult(result);
    } catch (err) {
      console.error("Discoverable passkey sign-in failed:", err);
      passkeyError = describePasskeyError(err, "login");
    } finally {
      isLoading = false;
    }
  }

  /** Username-first passkey sign-in. Also JavaScript-only, same reason. */
  async function handleUsernameLogin(event: SubmitEvent) {
    event.preventDefault();
    isLoading = true;
    passkeyError = null;

    try {
      const response = await loginOptions({ username: username.trim() });
      const options = parseCeremonyOptions<PublicKeyCredentialRequestOptionsJSON>(
        response.options
      );
      const challengeToken = response.challengeToken ?? "";

      const assertion = await startAuthentication({ optionsJSON: options });

      const result = await loginComplete({
        assertionResponseJson: JSON.stringify(assertion),
        challengeToken,
      });
      await handleAuthResult(result);
    } catch (err) {
      console.error("Username passkey sign-in failed:", err);
      passkeyError = describePasskeyError(err, "login");
    } finally {
      isLoading = false;
    }
  }

  function loginWithProvider(providerId: string) {
    isRedirecting = true;
    selectedProvider = providerId;

    const params = new URLSearchParams();
    params.set("provider", providerId);
    if (returnUrl && returnUrl !== "/") {
      params.set("returnUrl", returnUrl);
    }

    window.location.href = `/api/auth/oidc/login?${params.toString()}`;
  }

  function getButtonStyle(buttonColor?: string): string {
    if (!buttonColor) return "";
    return `background-color: ${buttonColor}; border-color: ${buttonColor};`;
  }

  function switchMode(newMode: LoginMode) {
    mode = newMode;
    passkeyError = null;
    recovery.clear();
    authenticator.clear();
    // Leaving the authenticator step abandons the passkey step it belonged to.
    if (newMode !== "totp") {
      stepUpToken = "";
      totpCode = "";
    }
  }
</script>

{#snippet providerIcon(name: string | undefined)}
  {#if name && name.toLowerCase().includes("google")}
    <img src="/logos/google.webp" alt="" class="mr-2 h-4 w-4 shrink-0 object-contain" aria-hidden="true" />
  {:else if name && name.toLowerCase().includes("apple")}
    <img src="/logos/apple.svg" alt="" class="mr-2 h-4 w-4 shrink-0 object-contain" aria-hidden="true" />
  {:else if name && name.toLowerCase().includes("github")}
    <img src="/logos/github.png" alt="" class="mr-2 h-4 w-4 shrink-0 object-contain" aria-hidden="true" />
  {:else}
    <ExternalLink class="mr-2 h-4 w-4" />
  {/if}
{/snippet}

{#snippet otherMethodLinks()}
  <Button
    variant="link"
    size="sm"
    class="h-auto p-0 text-xs"
    onclick={() => switchMode("recovery")}
    disabled={isLoading}
  >
    {signInMethodLabels.recoveryCode}
  </Button>
{/snippet}

{#snippet backToSignIn(label: string)}
  <Button
    variant="link"
    size="sm"
    class="h-auto p-0 text-xs"
    onclick={() => switchMode("default")}
    disabled={isLoading}
  >
    {label}
  </Button>
{/snippet}

{#if oidcQuery.loading}
  <div class="flex items-center justify-center p-8">
    <Loader2 class="h-8 w-8 animate-spin text-primary" />
  </div>
{:else}
  {@const oidc = oidcQuery.current}
  {@const hasOidc = oidc?.enabled && oidc.providers.length > 0}

  <div class="space-y-4">
    {#if tenantless}
      <p class="text-sm text-muted-foreground">
        Passkeys, authenticator codes, and recovery codes are checked against one
        tenant, so they are used at that tenant's own web address.
        {#if !hasOidc}
          Open your tenant's address to sign in.
        {/if}
      </p>
    {/if}

    {#if !passkeysSupported && !tenantless}
      <div class="flex items-start gap-3 rounded-md border border-yellow-500/30 bg-yellow-500/5 p-3">
        <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-yellow-600 dark:text-yellow-500" />
        <p class="text-sm text-yellow-700 dark:text-yellow-400">
          Your browser does not support passkeys. Use a recovery code, or try a different browser. An authenticator app is a second step after a passkey, so it cannot get you in on its own.
        </p>
      </div>
    {/if}

    <FormError issues={passkeyError} focusOnShow />

    {#if mode === "default"}
      {#if !tenantless}
        <!-- Primary: discoverable passkey sign-in. Needs JavaScript for the
             WebAuthn ceremony, so there is no server-side counterpart. -->
        <form onsubmit={handleDiscoverableLogin}>
          <Button
            type="submit"
            data-testid="passkey-sign-in"
            class="w-full h-12"
            size="lg"
            disabled={isLoading || isRedirecting || !passkeysSupported}
          >
            {#if isLoading}
              <Loader2 class="mr-2 h-5 w-5 animate-spin" />
              Waiting for passkey...
            {:else}
              <Fingerprint class="mr-2 h-5 w-5" />
              {signInMethodLabels.passkey}
            {/if}
          </Button>
        </form>

        <!-- Secondary: username-based sign-in -->
        <Button
          variant="outline"
          class="w-full"
          disabled={isLoading || isRedirecting || !passkeysSupported}
          onclick={() => switchMode("username")}
        >
          <User class="mr-2 h-4 w-4" />
          {signInMethodLabels.username}
        </Button>
      {/if}

      {#if hasOidc && oidc}
        {#if !tenantless}
          <div class="relative">
            <div class="absolute inset-0 flex items-center">
              <span class="w-full border-t"></span>
            </div>
            <div class="relative flex justify-center text-xs uppercase">
              <span class="bg-background px-2 text-muted-foreground">
                Or continue with
              </span>
            </div>
          </div>
        {/if}

        <div class="space-y-3">
          {#each oidc.providers as provider}
            <Button
              variant="outline"
              class="w-full h-11 relative"
              style={getButtonStyle(provider.buttonColor)}
              disabled={isLoading || isRedirecting || !provider.id}
              onclick={() => provider.id && loginWithProvider(provider.id)}
            >
              {#if isRedirecting && selectedProvider === provider.id}
                <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                Redirecting...
              {:else}
                {@render providerIcon(provider.name)}
                Sign in with {provider.name}
              {/if}
            </Button>
          {/each}
        </div>
      {/if}

      {#if !tenantless}
        <div class="flex justify-center gap-3 text-xs">
          {@render otherMethodLinks()}
        </div>
      {/if}

    {:else if mode === "username"}
      <!-- Username-first passkey sign-in. JavaScript-only, as above. -->
      <form onsubmit={handleUsernameLogin} class="space-y-3">
        <FormField label="Username" id="username" required>
          {#snippet control(field)}
            <div class="relative">
              <User class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                {...field}
                name="username"
                type="text"
                placeholder="your-username"
                class="pl-10"
                autocomplete="username webauthn"
                autocapitalize="none"
                spellcheck={false}
                autofocus
                bind:value={username}
                disabled={isLoading}
              />
            </div>
          {/snippet}
        </FormField>

        <Button
          type="submit"
          class="w-full"
          disabled={isLoading || !username.trim() || !passkeysSupported}
        >
          {#if isLoading}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-4 w-4" />
            Continue with passkey
          {/if}
        </Button>
      </form>

      <div class="flex justify-between text-xs">
        {@render backToSignIn("Back")}
        <div class="flex gap-3">
          {@render otherMethodLinks()}
        </div>
      </div>

    {:else if mode === "recovery"}
      <!-- Recovery-code sign-in. Verified entirely on the server, so this posts
           and works with JavaScript disabled. -->
      <form
        class="space-y-3"
        {...signInWithRecoveryCode.enhance(async ({ submit }) => {
          await recovery.run(submit, onSuccess);
        })}
      >
        <input type="hidden" name="returnUrl" value={returnUrl} />

        <FormError issues={recovery.error} focusOnShow />

        <FormField
          label="Username"
          id="recovery-username"
          required
          issues={signInWithRecoveryCode.fields.username.issues()}
        >
          {#snippet control(field)}
            <div class="relative">
              <User class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                {...field}
                name="username"
                type="text"
                placeholder="your-username"
                class="pl-10"
                autocomplete="username"
                autocapitalize="none"
                spellcheck={false}
                autofocus
                bind:value={username}
              />
            </div>
          {/snippet}
        </FormField>

        <FormField
          label="Recovery code"
          id="recovery-code"
          required
          issues={signInWithRecoveryCode.fields.code.issues()}
        >
          {#snippet control(field)}
            <div class="relative">
              <KeyRound class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
              <Input
                {...field}
                name="code"
                type="text"
                placeholder="XXXX-XXXX"
                class="pl-10 font-mono"
                autocomplete="one-time-code"
                autocapitalize="characters"
                spellcheck={false}
              />
            </div>
          {/snippet}
        </FormField>

        <Button
          type="submit"
          class="w-full"
          disabled={signInWithRecoveryCode.pending > 0}
        >
          {#if signInWithRecoveryCode.pending > 0}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Verifying...
          {:else}
            Verify recovery code
          {/if}
        </Button>
      </form>

      <div class="text-center">
        {@render backToSignIn("Back to sign in")}
      </div>

    {:else if mode === "totp"}
      <!-- Second step after the passkey: the authenticator code, verified on the server. -->
      <form
        bind:this={totpFormEl}
        class="space-y-3"
        {...signInWithAuthenticator.enhance(async ({ submit }) => {
          await authenticator.run(submit, onSuccess);
        })}
      >
        <input type="hidden" name="returnUrl" value={returnUrl} />
        <input type="hidden" name="stepUpToken" value={stepUpToken} />

        <FormError issues={authenticator.error} focusOnShow />

        <p class="text-sm text-muted-foreground">
          Your passkey was accepted. Enter the current code from your authenticator
          app to finish signing in.
        </p>

        <div class="space-y-2">
          <Label for="totp-code-input">Authenticator code</Label>
          <div class="flex justify-center">
            <InputOTP.Root
              name="code"
              inputId="totp-code-input"
              maxlength={6}
              bind:value={totpCode}
              onComplete={() => totpFormEl?.requestSubmit()}
            >
              {#snippet children({
                cells,
              }: {
                cells: ComponentProps<typeof InputOTP.Slot>["cell"][];
              })}
                <InputOTP.Group>
                  {#each cells.slice(0, 3) as cell}
                    <InputOTP.Slot {cell} />
                  {/each}
                </InputOTP.Group>
                <InputOTP.Separator />
                <InputOTP.Group>
                  {#each cells.slice(3, 6) as cell}
                    <InputOTP.Slot {cell} />
                  {/each}
                </InputOTP.Group>
              {/snippet}
            </InputOTP.Root>
          </div>
          {#each signInWithAuthenticator.fields.code.issues() ?? [] as issue}
            <p role="alert" class="text-center text-sm text-destructive">
              {issue.message}
            </p>
          {/each}
        </div>

        <Button
          type="submit"
          class="w-full"
          disabled={signInWithAuthenticator.pending > 0 ||
            !stepUpToken ||
            totpCode.length !== 6}
        >
          {#if signInWithAuthenticator.pending > 0}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Verifying...
          {:else}
            <Smartphone class="mr-2 h-4 w-4" />
            Verify
          {/if}
        </Button>
      </form>

      <div class="text-center">
        {@render backToSignIn("Back to sign in")}
      </div>
    {/if}
  </div>
{/if}
