<script lang="ts">
  import { resolve } from "$app/paths";
  import * as Card from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { FormError } from "#lib/forms/index.js";
  import { errorStatus } from "#lib/forms/submit-error.js";
  import Fingerprint from "@lucide/svelte/icons/fingerprint-pattern";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import Check from "@lucide/svelte/icons/check";
  import { Artwork } from "water-ui";
  import { fingerprintArtwork } from "#lib/watercolour-icons.js";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import {
    getAuthStatus,
    registerOptions,
    registerComplete,
    discoverableLoginOptions,
    loginOptions,
  } from "#lib/api/generated/passkeys.generated.remote.js";
  import { startRecoveryLink } from "#lib/api/generated/oidcs.generated.remote.js";
  import { getOidcProviders } from "../../auth.remote";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "#lib/components/auth/passkey-errors.js";
  import { runPasskeyAssertion } from "#lib/components/auth/passkey-login.js";
  import {
    completePasskeySignIn,
    settleSignIn,
    type SignInOutcome,
  } from "#lib/components/auth/sign-in-result.js";
  import { recoveredDestination } from "#lib/components/auth/recovered-method.js";
  import LoginForm from "#lib/components/auth/LoginForm.svelte";
  import RecoveryNextStep from "#lib/components/auth/RecoveryNextStep.svelte";
  import FirstRecoveryCodes from "#lib/components/auth/FirstRecoveryCodes.svelte";
  import OidcProviderButtons from "#lib/components/auth/OidcProviderButtons.svelte";
  import { goToReturnUrl } from "#lib/navigation/return-url.js";
  import { page } from "$app/state";

  /**
   * Where a spent recovery code lands. The account is named by the recovery session the
   * verify step set, so the username here is only what the new credential is labelled
   * with; naming someone else changes nothing about which account is enrolled.
   */
  const username = $derived(page.url.searchParams.get("username") ?? "");

  const providersQuery = getOidcProviders();
  const providers = $derived(providersQuery.current?.providers ?? []);
  const authStatusQuery = getAuthStatus();
  const emailRecoveryOffered = $derived(authStatusQuery.current?.emailRecoveryOffered ?? false);

  type Phase = "choose" | "signing-in" | "second-factor" | "sign-in-pending" | "recovery-codes";
  let phase = $state<Phase>("choose");
  let issuedRecoveryCodes = $state<string[]>([]);
  let isRegistering = $state(false);
  let isLinking = $state(false);
  let selectedProvider = $state<string | null>(null);
  let errorMessage = $state<string | null>(null);
  let sessionEnded = $state(false);
  let passkeyId = $state<string | null>(null);
  let stepUpToken = $state("");

  const destination = $derived(recoveredDestination(passkeyId));
  const loginUrl = $derived(resolve(`auth/login?${new URLSearchParams({ returnUrl: destination })}`));

  /** A 401 here means this screen's ten minutes ran out or its one enrolment was spent, whatever the call was. */
  function refusalMessage(err: unknown, fallback: string): string {
    sessionEnded = errorStatus(err) === 401;
    return sessionEnded ? "The recovery session has ended." : fallback;
  }

  /**
   * Enrols a passkey against the recovery session. The WebAuthn ceremony runs in the browser,
   * so this step needs JavaScript. The API issues no session for it, so a passkey that was
   * enrolled goes on to sign in the ordinary way.
   */
  async function handleRegister() {
    if (isRegistering || isLinking) return;
    isRegistering = true;
    errorMessage = null;
    sessionEnded = false;

    try {
      const response = await registerOptions({ username });
      const options =
        parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(
          response.options
        );

      const attestation = await startRegistration({ optionsJSON: options });

      const added = await registerComplete({
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken: response.challengeToken ?? "",
        label: username ? `${username}'s passkey` : undefined,
      });
      passkeyId = added.credentialId ?? null;
    } catch (err) {
      console.error("Recovery passkey registration failed:", err);
      errorMessage = describePasskeyError(
        err,
        "register",
        refusalMessage(
          err,
          "We could not register a passkey. Try again. This screen works for ten minutes after the code is accepted."
        )
      );
      isRegistering = false;
      return;
    }

    isRegistering = false;
    await signInWithNewPasskey();
  }

  async function newPasskeyOptions() {
    if (username) {
      try {
        return await loginOptions({ username });
      } catch {
        // The name here can be a display name, which no account answers to; the discoverable options need none.
      }
    }
    return discoverableLoginOptions();
  }

  function goToSignedInDestination() {
    return goToReturnUrl(destination, { replace: true, refreshAll: true });
  }

  /**
   * Signs in with the passkey just enrolled. Whatever stops it, the passkey is already saved, so
   * the person is asked to sign in with it rather than told recovery failed.
   */
  async function signInWithNewPasskey() {
    phase = "signing-in";
    let outcome: SignInOutcome | null;
    try {
      outcome = await settleSignIn(await runPasskeyAssertion(newPasskeyOptions, completePasskeySignIn));
    } catch {
      // The passkey is already saved, so any failure here asks for the sign-in instead.
      outcome = null;
    }

    if (outcome?.state === "signed-in" && outcome.recoveryCodes.length > 0) {
      issuedRecoveryCodes = outcome.recoveryCodes;
      phase = "recovery-codes";
    } else if (outcome?.state === "signed-in") {
      await goToSignedInDestination();
    } else if (outcome?.state === "second-factor") {
      stepUpToken = outcome.stepUpToken;
      phase = "second-factor";
    } else {
      phase = "sign-in-pending";
    }
  }

  /** Starts linking a provider. The API refuses a GET for this, so it is a command, not a link. */
  async function handleLinkProvider(providerId: string) {
    if (isRegistering || isLinking) return;
    isLinking = true;
    selectedProvider = providerId;
    errorMessage = null;
    sessionEnded = false;

    try {
      const { authorizationUrl } = await startRecoveryLink({
        providerId,
        returnUrl: recoveredDestination(),
      });
      if (!authorizationUrl) throw new Error("No authorization address");
      window.location.assign(authorizationUrl);
    } catch (err) {
      errorMessage = refusalMessage(
        err,
        "We could not link that provider. Try again, or create a passkey."
      );
      isLinking = false;
      selectedProvider = null;
    }
  }
</script>

<svelte:head>
  <title>Add a way to sign in - Nocturne</title>
  <meta name="robots" content="noindex, nofollow" />
</svelte:head>

<div class="flex min-h-screen items-center justify-center p-4">
  <Card.Root class="w-full max-w-md">
    <Card.Header class="text-center">
      <Artwork
        icon={fingerprintArtwork}
        palette="water"
        motion="auto"
        autoplay="once"
        class="mx-auto mb-2 size-56"
      />
      <Card.Title class="text-xl">Add a way to sign in</Card.Title>
      <Card.Description>
        {#if phase === "choose"}
          Your recovery code was accepted. It lets you add one way to sign in for
          {username || "your account"} and nothing else. This screen works for ten
          minutes.
        {:else if phase === "sign-in-pending"}
          Your passkey is ready. Sign in with it to finish.
        {:else if phase === "second-factor"}
          Your passkey is ready. One more step finishes signing in.
        {:else if phase === "recovery-codes"}
          Your passkey is ready.
        {:else}
          Your passkey is ready. Confirm it to finish signing in.
        {/if}
      </Card.Description>
    </Card.Header>

    <Card.Content class="space-y-4">
      {#if phase === "choose"}
        <FormError issues={errorMessage} focusOnShow />

        {#if sessionEnded}
          <RecoveryNextStep {emailRecoveryOffered} />
        {/if}

        {#if providers.length > 0}
          <OidcProviderButtons
            {providers}
            onLogin={handleLinkProvider}
            disabled={isRegistering || isLinking}
            isRedirecting={isLinking}
            {selectedProvider}
            dividerText="Or create a passkey"
          />
        {/if}

        <Button
          class="w-full"
          size="lg"
          onclick={handleRegister}
          disabled={isRegistering || isLinking}
        >
          {#if isRegistering}
            <Loader2 class="mr-2 h-5 w-5 animate-spin" />
            Waiting for passkey...
          {:else}
            <Fingerprint class="mr-2 h-5 w-5" />
            Create a passkey
          {/if}
        </Button>
      {:else if phase === "recovery-codes"}
        <FirstRecoveryCodes codes={issuedRecoveryCodes} onContinue={goToSignedInDestination} />
      {:else}
        <div
          class="flex items-start gap-3 rounded-md border border-success/20 bg-success/5 p-3"
        >
          <Check class="mt-0.5 h-4 w-4 shrink-0 text-success" />
          <p class="text-sm text-success">
            Passkey registered. Your recovery code is now used up.
          </p>
        </div>

        {#if phase === "signing-in"}
          <div class="flex items-center justify-center gap-2 text-sm text-muted-foreground">
            <Loader2 class="h-4 w-4 animate-spin" />
            Waiting for passkey...
          </div>
        {:else if phase === "second-factor"}
          <LoginForm returnUrl={destination} pendingStepUpToken={stepUpToken} />
        {:else}
          <Button class="w-full" size="lg" onclick={signInWithNewPasskey}>
            Sign in with your passkey
          </Button>
          <Button class="w-full" variant="outline" href={loginUrl}>
            Sign in on the sign-in page
          </Button>
        {/if}
      {/if}
    </Card.Content>

    <Card.Footer class="justify-center">
      <a href={resolve("auth/login")} class="text-xs text-muted-foreground hover:underline">
        Back to sign in
      </a>
    </Card.Footer>
  </Card.Root>
</div>
