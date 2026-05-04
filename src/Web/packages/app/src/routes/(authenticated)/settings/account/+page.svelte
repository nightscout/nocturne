<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import * as Dialog from "$lib/components/ui/dialog";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Separator } from "$lib/components/ui/separator";
  import {
    User,
    ShieldAlert,
    RefreshCw,
    Copy,
    Check,
    AlertTriangle,
    Loader2,
    Info,
    Server,
    Fingerprint,
    Smartphone,
  } from "lucide-svelte";
  import QRCode from "qrcode";
  import type { PageData } from "./$types";
  import { startRegistration } from "@simplewebauthn/browser";
  import {
    registerOptions,
    registerComplete,
    listCredentials,
    removeCredential,
    getRecoveryStatus,
    regenerateRecoveryCodes,
  } from "$lib/api/generated/passkeys.generated.remote";
  import {
    setup as totpSetup,
    verifySetup as totpVerifySetup,
    listCredentials as totpListCredentials,
    removeCredential as totpRemoveCredential,
  } from "$lib/api/generated/totps.generated.remote";
  import LinkedOidcIdentities from "$lib/components/settings/LinkedOidcIdentities.svelte";
  import UserProfileCard from "$lib/components/account/UserProfileCard.svelte";
  import SecurityCredentialCard from "$lib/components/account/SecurityCredentialCard.svelte";
  import TotpSetupDialog from "$lib/components/account/TotpSetupDialog.svelte";
  import { page } from "$app/state";

  const { data }: { data: PageData } = $props();

  const user = $derived(data.user);

  /** Handle logout */
  function handleLogout() {
    window.location.href = "/auth/logout";
  }

  // ============================================================================
  // Security State
  // ============================================================================

  const credentialsQuery = listCredentials();
  const recoveryQuery = getRecoveryStatus();

  let errorMessage = $state<string | null>(null);
  let successMessage = $state<string | null>(null);

  // Passkey add flow
  let isRegistering = $state(false);
  let showLabelDialog = $state(false);
  let newPasskeyLabel = $state("");

  // Passkey remove flow
  let isRemoving = $state<string | null>(null);
  let showRemoveDialog = $state(false);
  let removeTarget = $state<{ id?: string; label?: string | null } | null>(null);

  // Recovery codes
  let showRegenerateDialog = $state(false);
  let isRegenerating = $state(false);
  let showNewCodesDialog = $state(false);
  let newRecoveryCodes = $state<string[]>([]);
  let copiedCodes = $state(false);

  // ============================================================================
  // TOTP Authenticator State
  // ============================================================================

  const totpQuery = totpListCredentials();
  let showTotpSetup = $state(false);
  let totpSetupData = $state<{ provisioningUri?: string; base32Secret?: string; challengeToken?: string } | null>(null);
  let totpQrDataUrl = $state<string | null>(null);
  let totpVerifyCode = $state("");
  let totpLabel = $state("");
  let totpSetupLoading = $state(false);
  let totpSetupError = $state<string | null>(null);
  let totpRemovingId = $state<string | null>(null);
  let showTotpRemoveDialog = $state(false);
  let totpRemoveTarget = $state<{ id?: string; label?: string | null } | null>(null);

  const totpCredentials = $derived(totpQuery.current ?? []);
  const maxTotpCredentials = 10;

  const credentials = $derived(credentialsQuery.current?.credentials ?? []);
  const primaryAuthFactorCount = $derived(
    credentialsQuery.current?.primaryAuthFactorCount ?? 0
  );
  const recoveryStatus = $derived(recoveryQuery.current);
  const isSecurityLoading = $derived(credentialsQuery.loading);
  const canRemovePasskey = $derived(primaryAuthFactorCount > 1);

  // Handle ?linked= query param from OIDC link flow return
  $effect(() => {
    const linked = page.url.searchParams.get("linked");
    if (linked === "success") {
      successMessage = "Account linked successfully.";
      clearMessages();
    } else if (linked === "already") {
      successMessage = "This account was already linked.";
      clearMessages();
    }
    if (linked && typeof window !== "undefined") {
      const url = new URL(window.location.href);
      url.searchParams.delete("linked");
      history.replaceState({}, "", url.toString());
    }
  });
  const maxPasskeys = 20;

  // ============================================================================
  // Passkey registration
  // ============================================================================

  async function handleAddPasskey() {
    if (!user?.subjectId || !user?.name) return;
    isRegistering = true;
    errorMessage = null;

    try {
      const response = await registerOptions({ subjectId: user.subjectId, username: user.name });
      const options = JSON.parse(response.options ?? "");
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      await registerComplete({
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken,
      });

      newPasskeyLabel = "";
      showLabelDialog = true;
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Failed to register passkey.";
    } finally {
      isRegistering = false;
    }
  }

  function handleLabelDialogClose() {
    showLabelDialog = false;
    newPasskeyLabel = "";
    successMessage = "Passkey added successfully.";
    clearMessages();
  }

  // ============================================================================
  // Passkey removal
  // ============================================================================

  function confirmRemovePasskey(credential: { id?: string; label?: string | null }) {
    removeTarget = credential;
    showRemoveDialog = true;
  }

  async function handleRemovePasskey() {
    if (!removeTarget?.id) return;
    isRemoving = removeTarget.id;
    errorMessage = null;
    showRemoveDialog = false;

    try {
      await removeCredential(removeTarget.id);
      successMessage = "Passkey removed.";
      clearMessages();
    } catch (err) {
      errorMessage =
        err instanceof Error ? err.message : "Failed to remove passkey.";
    } finally {
      isRemoving = null;
      removeTarget = null;
    }
  }

  // ============================================================================
  // Recovery codes
  // ============================================================================

  async function handleRegenerateCodes() {
    isRegenerating = true;
    errorMessage = null;
    showRegenerateDialog = false;

    try {
      const result = await regenerateRecoveryCodes();
      newRecoveryCodes = result.codes ?? [];
      showNewCodesDialog = true;
    } catch (err) {
      errorMessage = "Failed to regenerate recovery codes.";
    } finally {
      isRegenerating = false;
    }
  }

  async function copyRecoveryCodes() {
    const text = newRecoveryCodes.join("\n");
    await navigator.clipboard.writeText(text);
    copiedCodes = true;
    setTimeout(() => (copiedCodes = false), 2000);
  }

  // ============================================================================
  // TOTP Authenticator Management
  // ============================================================================

  async function handleStartTotpSetup() {
    totpSetupLoading = true;
    totpSetupError = null;
    totpVerifyCode = "";
    totpLabel = "";
    totpQrDataUrl = null;

    try {
      const result = await totpSetup();
      totpSetupData = {
        provisioningUri: result.provisioningUri,
        base32Secret: result.base32Secret,
        challengeToken: result.challengeToken,
      };

      if (result.provisioningUri) {
        totpQrDataUrl = await QRCode.toDataURL(result.provisioningUri, {
          width: 200,
          margin: 2,
          color: { dark: "#000000", light: "#ffffff" },
        });
      }

      showTotpSetup = true;
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Failed to start authenticator setup.";
    } finally {
      totpSetupLoading = false;
    }
  }

  async function handleCompleteTotpSetup() {
    if (totpSetupLoading) return;
    if (!totpSetupData?.challengeToken || totpVerifyCode.length !== 6) return;
    totpSetupLoading = true;
    totpSetupError = null;

    try {
      await totpVerifySetup({
        challengeToken: totpSetupData.challengeToken,
        code: totpVerifyCode,
        label: totpLabel.trim() || undefined,
      });

      showTotpSetup = false;
      totpSetupData = null;
      totpQrDataUrl = null;
      totpVerifyCode = "";
      totpLabel = "";
      successMessage = "Authenticator app added successfully.";
      clearMessages();
    } catch (err) {
      totpSetupError = err instanceof Error ? err.message : "Verification failed. Check the code and try again.";
    } finally {
      totpSetupLoading = false;
    }
  }

  function confirmRemoveTotp(credential: { id?: string; label?: string | null }) {
    totpRemoveTarget = credential;
    showTotpRemoveDialog = true;
  }

  async function handleRemoveTotp() {
    if (!totpRemoveTarget?.id) return;
    totpRemovingId = totpRemoveTarget.id;
    errorMessage = null;
    showTotpRemoveDialog = false;

    try {
      await totpRemoveCredential(totpRemoveTarget.id);
      successMessage = "Authenticator removed.";
      clearMessages();
    } catch (err) {
      errorMessage = err instanceof Error ? err.message : "Failed to remove authenticator.";
    } finally {
      totpRemovingId = null;
      totpRemoveTarget = null;
    }
  }

  function clearMessages() {
    setTimeout(() => {
      successMessage = null;
      errorMessage = null;
    }, 3000);
  }
</script>

<svelte:head>
  <title>Account - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  {#if user}
    <div class="flex items-center gap-3">
      <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
        <User class="h-6 w-6 text-primary" />
      </div>
      <div>
        <h1 class="text-2xl font-bold tracking-tight">Account</h1>
        <p class="text-muted-foreground">
          Manage your account and security settings
        </p>
      </div>
    </div>

    <!-- User Profile Card -->
    <UserProfileCard {user} onLogout={handleLogout} />

    {#if errorMessage}
      <div
        class="flex items-start gap-3 rounded-md border border-destructive/20 bg-destructive/5 p-3"
      >
        <AlertTriangle class="mt-0.5 h-4 w-4 shrink-0 text-destructive" />
        <p class="text-sm text-destructive">{errorMessage}</p>
      </div>
    {/if}

    {#if successMessage}
      <div
        class="flex items-start gap-3 rounded-md border border-green-200 bg-green-50 p-3 dark:border-green-900/50 dark:bg-green-900/20"
      >
        <Check
          class="mt-0.5 h-4 w-4 shrink-0 text-green-600 dark:text-green-400"
        />
        <p class="text-sm text-green-800 dark:text-green-200">
          {successMessage}
        </p>
      </div>
    {/if}

    {#if isSecurityLoading}
      <Card.Root>
        <Card.Content class="flex items-center justify-center py-12">
          <Loader2 class="h-6 w-6 animate-spin text-muted-foreground" />
        </Card.Content>
      </Card.Root>
    {:else}
      <!-- Section 1: Registered Passkeys -->
      <SecurityCredentialCard
        title="Passkeys"
        description="Passkeys provide secure, phishing-resistant authentication using your device's biometrics or security key."
        icon={Fingerprint}
        addLabel="Add passkey"
        credentials={credentials.map((c) => ({
          id: c.id ?? "",
          label: c.label,
          createdAt: c.createdAt,
          lastUsedAt: c.lastUsedAt,
        }))}
        isAdding={isRegistering}
        isRemoving={isRemoving !== null}
        removingId={isRemoving ?? undefined}
        canRemove={canRemovePasskey}
        maxCredentials={maxPasskeys}
        onAdd={handleAddPasskey}
        onRemove={(cred) => confirmRemovePasskey(cred)}
      />

      <!-- Section 1b: Linked OIDC identities -->
      <LinkedOidcIdentities {primaryAuthFactorCount} />

      <!-- Section 2: Authenticator Apps -->
      <SecurityCredentialCard
        title="Authenticator Apps"
        description="Use an authenticator app like Google Authenticator or Authy to generate time-based one-time passwords for sign-in."
        icon={Smartphone}
        addLabel="Add authenticator"
        credentials={totpCredentials.map((c) => ({
          id: c.id ?? "",
          label: c.label,
          createdAt: c.createdAt,
          lastUsedAt: c.lastUsedAt,
        }))}
        isAdding={totpSetupLoading}
        isRemoving={totpRemovingId !== null}
        removingId={totpRemovingId ?? undefined}
        canRemove={true}
        maxCredentials={maxTotpCredentials}
        onAdd={handleStartTotpSetup}
        onRemove={(cred) => confirmRemoveTotp(cred)}
      />

      <!-- Section 3: Recovery Codes -->
      <Card.Root>
        <Card.Header>
          <Card.Title class="flex items-center gap-2">
            <ShieldAlert class="h-5 w-5" />
            Recovery Codes
          </Card.Title>
          <Card.Description>
            Recovery codes allow you to access your account if you lose all your
            passkeys. Store them in a safe place.
          </Card.Description>
        </Card.Header>
        <Card.Content class="space-y-4">
          {#if recoveryStatus}
            <div class="flex items-center justify-between">
              <div class="space-y-1">
                <p class="text-sm font-medium">
                  {recoveryStatus.remainingCodes} of {recoveryStatus.totalCodes} recovery
                  codes remaining
                </p>
                <p class="text-xs text-muted-foreground">
                  Each code can only be used once.
                </p>
              </div>
              <Badge
                variant={(recoveryStatus.remainingCodes ?? 0) > 2
                  ? "secondary"
                  : "destructive"}
              >
                {recoveryStatus.remainingCodes} remaining
              </Badge>
            </div>
          {:else}
            <p class="text-sm text-muted-foreground">
              No recovery codes have been generated yet.
            </p>
          {/if}

          <Separator />

          <Button
            variant="outline"
            disabled={isRegenerating}
            onclick={() => (showRegenerateDialog = true)}
          >
            {#if isRegenerating}
              <Loader2 class="mr-1.5 h-4 w-4 animate-spin" />
            {:else}
              <RefreshCw class="mr-1.5 h-4 w-4" />
            {/if}
            Regenerate recovery codes
          </Button>
        </Card.Content>
      </Card.Root>

      <!-- Section 4: Recovery Mode Info -->
      <Card.Root class="border-muted">
        <Card.Header>
          <Card.Title class="flex items-center gap-2 text-muted-foreground">
            <Server class="h-5 w-5" />
            Server-Side Account Recovery
          </Card.Title>
        </Card.Header>
        <Card.Content>
          <div
            class="flex items-start gap-3 rounded-md border border-border bg-muted/30 p-4"
          >
            <Info class="mt-0.5 h-5 w-5 shrink-0 text-muted-foreground" />
            <div class="space-y-2">
              <p class="text-sm text-muted-foreground">
                If you lose all your passkeys and recovery codes, you can recover
                your account by setting the
                <code
                  class="rounded bg-muted px-1.5 py-0.5 text-xs font-mono text-foreground"
                >
                  NOCTURNE_RECOVERY_MODE
                </code>
                environment variable on your server.
              </p>
              <p class="text-sm text-muted-foreground">
                This enables a temporary recovery flow that allows you to register
                a new passkey. It requires physical access to the server
                environment.
              </p>
            </div>
          </div>
        </Card.Content>
      </Card.Root>
    {/if}
  {:else}
    <!-- Not logged in -->
    <div
      class="min-h-[70vh] flex flex-col items-center justify-center p-4 animate-in fade-in slide-in-from-bottom-4 duration-500"
    >
      <Card.Root class="w-full max-w-md text-center shadow-lg">
        <Card.Header class="pb-4 pt-8">
          <div
            class="mx-auto w-16 h-16 rounded-full bg-primary/10 flex items-center justify-center mb-6"
          >
            <User class="h-8 w-8 text-primary" />
          </div>
          <Card.Title class="text-2xl font-bold">Not Signed In</Card.Title>
          <Card.Description class="text-base mt-2">
            Sign in to access your account dashboard and manage your settings.
          </Card.Description>
        </Card.Header>
        <Card.Content class="pb-8">
          <Button
            href="/auth/login"
            size="lg"
            class="w-full sm:w-auto min-w-[200px] font-medium"
          >
            <User class="mr-2 h-5 w-5" />
            Sign In with Nocturne
          </Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/if}
</div>

<!-- Label Dialog (after passkey registration) -->
<Dialog.Root bind:open={showLabelDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Name your passkey</Dialog.Title>
      <Dialog.Description>
        Give this passkey a name to help you identify it later (e.g. "MacBook
        Touch ID", "YubiKey 5").
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="space-y-2">
        <Label for="passkey-label">Label (optional)</Label>
        <Input
          id="passkey-label"
          type="text"
          placeholder="e.g. MacBook Touch ID"
          bind:value={newPasskeyLabel}
        />
      </div>
    </div>
    <Dialog.Footer>
      <Button variant="outline" onclick={handleLabelDialogClose}>
        Skip
      </Button>
      <Button onclick={handleLabelDialogClose}>
        Save
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Remove Confirmation Dialog -->
<Dialog.Root bind:open={showRemoveDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Remove passkey</Dialog.Title>
      <Dialog.Description>
        Are you sure you want to remove "{removeTarget?.label ??
          "Unnamed passkey"}"? You will no longer be able to sign in with this
        passkey.
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showRemoveDialog = false)}>
        Cancel
      </Button>
      <Button variant="destructive" onclick={handleRemovePasskey}>
        Remove
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- Regenerate Confirmation Dialog -->
<Dialog.Root bind:open={showRegenerateDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Regenerate recovery codes</Dialog.Title>
      <Dialog.Description>
        This will invalidate all existing recovery codes and generate new ones.
        Make sure to save the new codes in a safe place.
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button
        variant="outline"
        onclick={() => (showRegenerateDialog = false)}
      >
        Cancel
      </Button>
      <Button variant="destructive" onclick={handleRegenerateCodes}>
        Regenerate
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<!-- New Recovery Codes Dialog -->
<Dialog.Root bind:open={showNewCodesDialog}>
  <Dialog.Content class="max-w-md">
    <Dialog.Header>
      <Dialog.Title>New recovery codes</Dialog.Title>
      <Dialog.Description>
        Save these codes in a safe place. Each code can only be used once. This
        is the only time they will be shown.
      </Dialog.Description>
    </Dialog.Header>
    <div class="space-y-4 py-4">
      <div class="grid grid-cols-2 gap-2 rounded-md border bg-muted/30 p-4">
        {#each newRecoveryCodes as code}
          <p class="font-mono text-sm text-center">{code}</p>
        {/each}
      </div>
      <Button variant="outline" class="w-full" onclick={copyRecoveryCodes}>
        {#if copiedCodes}
          <Check class="mr-1.5 h-4 w-4 text-green-600" />
          Copied
        {:else}
          <Copy class="mr-1.5 h-4 w-4" />
          Copy all codes
        {/if}
      </Button>
    </div>
    <Dialog.Footer>
      <Button
        onclick={() => {
          showNewCodesDialog = false;
          newRecoveryCodes = [];
          copiedCodes = false;
        }}
      >
        Done
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>

<TotpSetupDialog
  bind:open={showTotpSetup}
  qrDataUrl={totpQrDataUrl ?? ""}
  secret={totpSetupData?.base32Secret ?? ""}
  bind:verifyCode={totpVerifyCode}
  bind:label={totpLabel}
  bind:loading={totpSetupLoading}
  bind:error={totpSetupError}
  onVerify={handleCompleteTotpSetup}
  onCancel={() => {
    showTotpSetup = false;
    totpSetupData = null;
    totpQrDataUrl = null;
    totpVerifyCode = "";
    totpLabel = "";
    totpSetupError = null;
  }}
/>

<!-- TOTP Remove Confirmation Dialog -->
<Dialog.Root bind:open={showTotpRemoveDialog}>
  <Dialog.Content>
    <Dialog.Header>
      <Dialog.Title>Remove authenticator</Dialog.Title>
      <Dialog.Description>
        Are you sure you want to remove "{totpRemoveTarget?.label ??
          "Unnamed authenticator"}"? You will no longer be able to sign in with
        this authenticator app.
      </Dialog.Description>
    </Dialog.Header>
    <Dialog.Footer>
      <Button variant="outline" onclick={() => (showTotpRemoveDialog = false)}>
        Cancel
      </Button>
      <Button variant="destructive" onclick={handleRemoveTotp}>
        Remove
      </Button>
    </Dialog.Footer>
  </Dialog.Content>
</Dialog.Root>
