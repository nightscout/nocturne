<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Loader2,
    ExternalLink,
    Fingerprint,
    User,
    KeyRound,
    AlertTriangle,
    Smartphone,
    ShieldAlert,
  } from "lucide-svelte";
  import * as InputOTP from "$lib/components/ui/input-otp";
  import { login as totpLogin } from "$lib/api/generated/totps.generated.remote";
  import { startAuthentication } from "@simplewebauthn/browser";
  import { getOidcProviders, setAuthCookies } from "$routes/(unauthenticated)/auth/auth.remote";
  import {
    discoverableLoginOptions,
    loginOptions,
    loginComplete,
    recoveryVerify,
  } from "$lib/api/generated/passkeys.generated.remote";
  import { goto, invalidateAll } from "$app/navigation";

  interface Props {
    returnUrl?: string;
    onSuccess?: () => void;
  }

  let { returnUrl = "/", onSuccess }: Props = $props();

  const oidcQuery = getOidcProviders();

  // UI mode
  type LoginMode = "default" | "username" | "recovery" | "totp";
  let mode = $state<LoginMode>("default");
  let isLoading = $state(false);
  let errorMessage = $state<string | null>(null);
  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);

  // Browser support
  let passkeysSupported = $state(typeof window !== "undefined" && window.PublicKeyCredential !== undefined);

  // Form fields
  let username = $state("");
  let recoveryCode = $state("");
  let totpCode = $state("");

  async function handleAuthResult(result: {
    success?: boolean;
    accessToken?: string;
    refreshToken?: string;
    expiresIn?: number;
    refreshExpiresIn?: number;
    error?: string;
  }) {
    if (!result.success) {
      errorMessage = result.error || "Authentication failed";
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

  async function handleDiscoverableLogin() {
    isLoading = true;
    errorMessage = null;

    try {
      const response = await discoverableLoginOptions();
      const options = JSON.parse(response.options ?? "");
      const challengeToken = response.challengeToken ?? "";

      const assertion = await startAuthentication({ optionsJSON: options });

      const result = await loginComplete({
        assertionResponseJson: JSON.stringify(assertion),
        challengeToken,
      });
      await handleAuthResult(result);
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Authentication failed";
    } finally {
      isLoading = false;
    }
  }

  async function handleUsernameLogin() {
    if (!username.trim()) {
      errorMessage = "Please enter your username";
      return;
    }

    isLoading = true;
    errorMessage = null;

    try {
      const response = await loginOptions({ username: username.trim() });
      const options = JSON.parse(response.options ?? "");
      const challengeToken = response.challengeToken ?? "";

      const assertion = await startAuthentication({ optionsJSON: options });

      const result = await loginComplete({
        assertionResponseJson: JSON.stringify(assertion),
        challengeToken,
      });
      await handleAuthResult(result);
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Authentication failed";
    } finally {
      isLoading = false;
    }
  }

  async function handleRecoveryCode() {
    if (!username.trim() || !recoveryCode.trim()) {
      errorMessage = "Please enter your username and recovery code";
      return;
    }

    isLoading = true;
    errorMessage = null;

    try {
      const result = await recoveryVerify({ username: username.trim(), code: recoveryCode.trim() });
      await handleAuthResult(result);
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Recovery code verification failed";
    } finally {
      isLoading = false;
    }
  }

  async function handleTotpLogin() {
    if (isLoading) return;
    if (!username.trim() || totpCode.length !== 6) {
      errorMessage = "Please enter your username and 6-digit code";
      return;
    }
    isLoading = true;
    errorMessage = null;
    try {
      const result = await totpLogin({ username: username.trim(), code: totpCode });
      await handleAuthResult(result);
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Authentication failed";
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
    errorMessage = null;
  }
</script>

{#if oidcQuery.loading}
  <div class="flex items-center justify-center p-8">
    <Loader2 class="h-8 w-8 animate-spin text-primary" />
  </div>
{:else}
  {@const oidc = oidcQuery.current}
  {@const hasOidc = oidc?.enabled && oidc.providers.length > 0}

  <div class="space-y-4">
    {#if !passkeysSupported}
      <div class="flex items-start gap-3 rounded-md border border-yellow-500/30 bg-yellow-500/5 p-3">
        <ShieldAlert class="mt-0.5 h-4 w-4 shrink-0 text-yellow-600 dark:text-yellow-500" />
        <p class="text-sm text-yellow-700 dark:text-yellow-400">
          Your browser does not support passkeys. Use an authenticator app, a recovery code, or try a different browser.
        </p>
      </div>
    {/if}

    {#if errorMessage}
      <div class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3">
        <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {/if}

    {#if mode === "default"}
      <!-- Primary: Discoverable passkey login -->
      <Button
        class="w-full h-12"
        size="lg"
        disabled={isLoading || isRedirecting || !passkeysSupported}
        onclick={handleDiscoverableLogin}
      >
        {#if isLoading}
          <Loader2 class="mr-2 h-5 w-5 animate-spin" />
          Waiting for passkey...
        {:else}
          <Fingerprint class="mr-2 h-5 w-5" />
          Sign in with passkey
        {/if}
      </Button>

      <!-- Secondary: Username-based login -->
      <Button
        variant="outline"
        class="w-full"
        disabled={isLoading || isRedirecting || !passkeysSupported}
        onclick={() => switchMode("username")}
      >
        <User class="mr-2 h-4 w-4" />
        Sign in with username
      </Button>

      {#if hasOidc && oidc}
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
                <ExternalLink class="mr-2 h-4 w-4" />
                Sign in with {provider.name}
              {/if}
            </Button>
          {/each}
        </div>
      {/if}

      <div class="flex justify-center gap-3 text-xs">
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground underline"
          onclick={() => switchMode("totp")}
          disabled={isLoading}
        >
          Use authenticator app
        </button>
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground underline"
          onclick={() => switchMode("recovery")}
          disabled={isLoading}
        >
          Use a recovery code
        </button>
      </div>

    {:else if mode === "username"}
      <!-- Username-based passkey login -->
      <div class="space-y-3">
        <div class="space-y-2">
          <Label for="username">Username</Label>
          <div class="relative">
            <User class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              id="username"
              type="text"
              placeholder="your-username"
              class="pl-10"
              bind:value={username}
              disabled={isLoading}
              onkeydown={(e) => e.key === "Enter" && handleUsernameLogin()}
            />
          </div>
        </div>

        <Button
          class="w-full"
          disabled={isLoading || !username.trim() || !passkeysSupported}
          onclick={handleUsernameLogin}
        >
          {#if isLoading}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-4 w-4" />
            Continue with passkey
          {/if}
        </Button>
      </div>

      <div class="flex justify-between text-xs">
        <button
          type="button"
          class="text-muted-foreground hover:text-foreground underline"
          onclick={() => switchMode("default")}
          disabled={isLoading}
        >
          Back
        </button>
        <div class="flex gap-3">
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground underline"
            onclick={() => switchMode("totp")}
            disabled={isLoading}
          >
            Use authenticator app
          </button>
          <button
            type="button"
            class="text-muted-foreground hover:text-foreground underline"
            onclick={() => switchMode("recovery")}
            disabled={isLoading}
          >
            Use a recovery code
          </button>
        </div>
      </div>

    {:else if mode === "recovery"}
      <!-- Recovery code login -->
      <div class="space-y-3">
        <div class="space-y-2">
          <Label for="recovery-username">Username</Label>
          <div class="relative">
            <User class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              id="recovery-username"
              type="text"
              placeholder="your-username"
              class="pl-10"
              bind:value={username}
              disabled={isLoading}
            />
          </div>
        </div>

        <div class="space-y-2">
          <Label for="recovery-code">Recovery code</Label>
          <div class="relative">
            <KeyRound class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              id="recovery-code"
              type="text"
              placeholder="XXXX-XXXX"
              class="pl-10 font-mono"
              bind:value={recoveryCode}
              disabled={isLoading}
              onkeydown={(e) => e.key === "Enter" && handleRecoveryCode()}
            />
          </div>
        </div>

        <Button
          class="w-full"
          disabled={isLoading || !username.trim() || !recoveryCode.trim()}
          onclick={handleRecoveryCode}
        >
          {#if isLoading}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Verifying...
          {:else}
            Verify recovery code
          {/if}
        </Button>
      </div>

      <div class="text-center">
        <button
          type="button"
          class="text-xs text-muted-foreground hover:text-foreground underline"
          onclick={() => switchMode("default")}
          disabled={isLoading}
        >
          Back to sign in
        </button>
      </div>

    {:else if mode === "totp"}
      <!-- TOTP authenticator login -->
      <div class="space-y-3">
        <div class="space-y-2">
          <Label for="totp-username">Username</Label>
          <div class="relative">
            <User class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground" />
            <Input
              id="totp-username"
              type="text"
              placeholder="your-username"
              class="pl-10"
              bind:value={username}
              disabled={isLoading}
            />
          </div>
        </div>

        <div class="space-y-2">
          <Label>Authenticator code</Label>
          <div class="flex justify-center">
            <InputOTP.Root maxlength={6} bind:value={totpCode} onComplete={handleTotpLogin}>
              {#snippet children({ cells })}
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
        </div>

        <Button
          class="w-full"
          disabled={isLoading || !username.trim() || totpCode.length !== 6}
          onclick={handleTotpLogin}
        >
          {#if isLoading}
            <Loader2 class="mr-2 h-4 w-4 animate-spin" />
            Verifying...
          {:else}
            <Smartphone class="mr-2 h-4 w-4" />
            Verify
          {/if}
        </Button>
      </div>

      <div class="text-center">
        <button
          type="button"
          class="text-xs text-muted-foreground hover:text-foreground underline"
          onclick={() => switchMode("default")}
          disabled={isLoading}
        >
          Back to sign in
        </button>
      </div>
    {/if}
  </div>
{/if}
