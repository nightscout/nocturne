<script lang="ts">
  import * as Card from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import {
    Loader2,
    Check,
    AlertTriangle,
    UserPlus,
  } from "lucide-svelte";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";
  import {
    getInviteInfo,
    acceptInvite,
  } from "$lib/api/generated/memberInvites.generated.remote";
  import {
    inviteOptions,
    inviteComplete,
  } from "$lib/api/generated/passkeys.generated.remote";
  import {
    getOidcProviders,
    setAuthCookies,
  } from "$routes/(unauthenticated)/auth/auth.remote";
  import RecoveryCodes from "$lib/components/auth/RecoveryCodes.svelte";
  import OidcProviderButtons from "$lib/components/auth/OidcProviderButtons.svelte";
  import PasskeyRegistrationForm from "$lib/components/auth/PasskeyRegistrationForm.svelte";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "$lib/components/auth/passkey-errors";
  import { FormError, describeSubmitError } from "$lib/forms";
  import { retainQuery } from "$lib/api/retain-query.svelte";

  // ── URL params ────────────────────────────────────────────────────
  const token = $derived(page.url.searchParams.get("token") ?? "");

  // ── Remote data ───────────────────────────────────────────────────
  const inviteInfoQuery = $derived(token ? getInviteInfo(token) : undefined);
  retainQuery(() => inviteInfoQuery);
  const oidcQuery = getOidcProviders();

  const inviteInfo = $derived(inviteInfoQuery?.current);
  const oidc = $derived(oidcQuery.current);
  const hasOidc = $derived(oidc?.enabled && (oidc?.providers?.length ?? 0) > 0);

  // Who is looking at this invite. The session cookie is domain-wide, so someone who follows
  // another patient on this instance arrives here already signed in; the backend reports that
  // against the invite rather than against tenant membership, which they do not yet have.
  const viewer = $derived(inviteInfo?.viewer);
  const isSignedIn = $derived(Boolean(viewer?.subjectId));
  const isAlreadyMember = $derived(viewer?.isMember ?? false);

  // ── Invite validity ───────────────────────────────────────────────
  // An invite that can no longer be accepted comes back as an error carrying the reason, not as a
  // record with its validity flags set — the details of a spent invite aren't served to whoever
  // holds the link.
  const inviteError = $derived.by(() => {
    if (!token) return "No invite token provided. Please check the link you were given.";
    if (inviteInfoQuery?.error)
      return describeSubmitError(
        inviteInfoQuery.error,
        "This invite link is invalid or has expired."
      );
    return null;
  });

  // ── Accept invite (authenticated) ────────────────────────────────
  let isAccepting = $state(false);
  let acceptError = $state<string | null>(null);

  async function handleAcceptInvite() {
    isAccepting = true;
    acceptError = null;
    try {
      await acceptInvite(token);
      await goto("/", { replaceState: true });
    } catch (err) {
      console.error("Accepting the invite failed:", err);
      acceptError = describeSubmitError(
        err,
        "We couldn't add you to this instance. Please try again in a moment."
      );
    } finally {
      isAccepting = false;
    }
  }

  // ── OIDC login ───────────────────────────────────────────────────
  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);

  function loginWithProvider(providerId: string) {
    isRedirecting = true;
    selectedProvider = providerId;
    const params = new URLSearchParams();
    params.set("provider", providerId);
    params.set("returnUrl", `/join?token=${encodeURIComponent(token)}`);
    window.location.href = `/api/auth/oidc/login?${params.toString()}`;
  }

  // ── Passkey registration ─────────────────────────────────────────
  let isRegistering = $state(false);
  let registrationComplete = $state(false);
  let recoveryCodes = $state<string[]>([]);
  let passkeyError = $state<string | null>(null);

  async function handlePasskeyRegister(username: string, displayName: string) {
    isRegistering = true;
    passkeyError = null;

    try {
      const response = await inviteOptions({
        token,
        username,
        displayName,
      });
      const options = parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
        response.options
      );
      const challengeToken = response.challengeToken ?? "";

      const attestation = await startRegistration({ optionsJSON: options });

      const result = await inviteComplete({
        token,
        username,
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
      console.error("Passkey registration from invite failed:", err);
      passkeyError = describePasskeyError(
        err,
        "register",
        "We couldn't create your account. Please try again."
      );
    } finally {
      isRegistering = false;
    }
  }

  function goHome() {
    goto("/", { replaceState: true });
  }
</script>

<svelte:head>
  <title>Join - Nocturne</title>
</svelte:head>

<div class="flex flex-1 items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    {#if inviteError}
      <!-- Error state -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
        >
          <AlertTriangle class="h-6 w-6 text-destructive" />
        </div>
        <Card.Title class="text-2xl font-bold">Invite Unavailable</Card.Title>
        <Card.Description>{inviteError}</Card.Description>
      </Card.Header>
    {:else if !inviteInfo}
      <!-- Loading state -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Loader2 class="h-6 w-6 text-primary animate-spin" />
        </div>
        <Card.Title class="text-2xl font-bold">Loading invite...</Card.Title>
      </Card.Header>
    {:else if registrationComplete}
      <!-- Passkey registration complete — show recovery codes -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-green-500/10"
        >
          <Check class="h-6 w-6 text-green-600" />
        </div>
        <Card.Title class="text-2xl font-bold">You're In</Card.Title>
        <Card.Description>
          Your account has been created and you've joined
          {inviteInfo.tenantName ?? "the site"}.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <RecoveryCodes
          codes={recoveryCodes}
          onContinue={goHome}
          continueLabel="Continue to Nocturne"
        />
      </Card.Content>
    {:else if isAlreadyMember}
      <!-- Signed in and already a member of this site -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <Check class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">You're Already In</Card.Title>
        <Card.Description>
          You already have access to
          <strong>{inviteInfo.tenantName ?? "this site"}</strong>.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <Button class="w-full" size="lg" onclick={goHome}>
          Continue to Nocturne
        </Button>
      </Card.Content>
    {:else if isSignedIn}
      <!-- Signed in as someone who is not a member yet — just accept the invite -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <UserPlus class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Accept Invite</Card.Title>
        <Card.Description>
          {#if inviteInfo.createdByName}
            <strong>{inviteInfo.createdByName}</strong> has invited you to join
          {:else}
            You've been invited to join
          {/if}
          <strong>{inviteInfo.tenantName ?? "a site"}</strong>
          {#if viewer?.name}
            as <strong>{viewer.name}</strong>
          {/if}.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <div class="space-y-4">
          <FormError issues={acceptError} focusOnShow />

          <Button
            class="w-full"
            size="lg"
            disabled={isAccepting}
            onclick={handleAcceptInvite}
          >
            {#if isAccepting}
              <Loader2 class="mr-2 h-5 w-5 animate-spin" />
              Joining...
            {:else}
              <UserPlus class="mr-2 h-5 w-5" />
              Accept Invite
            {/if}
          </Button>
        </div>
      </Card.Content>
    {:else}
      <!-- Not authenticated — show OIDC + passkey options -->
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <UserPlus class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Join Nocturne</Card.Title>
        <Card.Description>
          {#if inviteInfo.createdByName}
            <strong>{inviteInfo.createdByName}</strong> has invited you to join
          {:else}
            You've been invited to join
          {/if}
          <strong>{inviteInfo.tenantName ?? "a site"}</strong>.
          Create an account or sign in to accept.
        </Card.Description>
      </Card.Header>

      <Card.Content>
        <div class="space-y-4">
          <FormError issues={passkeyError} focusOnShow />

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
