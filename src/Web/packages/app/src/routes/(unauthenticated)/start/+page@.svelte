<script lang="ts">
  import { Button } from "#lib/components/ui/button/index.js";
  import { Input } from "#lib/components/ui/input/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Progress } from "#lib/components/ui/progress/index.js";
  import Loader2 from "@lucide/svelte/icons/loader-circle";
  import AlertTriangle from "@lucide/svelte/icons/triangle-alert";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import Bookmark from "@lucide/svelte/icons/bookmark";
  import Check from "@lucide/svelte/icons/check";
  import Fingerprint from "@lucide/svelte/icons/fingerprint";
  import Globe from "@lucide/svelte/icons/globe";
  import Lock from "@lucide/svelte/icons/lock";
  import Smartphone from "@lucide/svelte/icons/smartphone";
  import QRCode from "qrcode";
  import {
    startRegistration,
    type PublicKeyCredentialCreationOptionsJSON,
  } from "@simplewebauthn/browser";
  import { page } from "$app/state";
  import { resolve } from "$app/paths";
  import {
    getInfo,
    validateSlug,
    redeem,
  } from "#lib/api/generated/tenantCreationInvites.generated.remote.js";
  import {
    tenantInviteOptions,
    tenantInviteComplete,
  } from "#lib/api/generated/passkeys.generated.remote.js";
  import {
    getOidcProviders,
    setAuthCookies,
  } from "#routes/(unauthenticated)/auth/auth.remote.js";
  import SetupChrome from "#routes/(unauthenticated)/setup/SetupChrome.svelte";
  import StepArtwork, { type StepArt } from "#routes/(unauthenticated)/setup/StepArtwork.svelte";
  import RecoveryCodes from "#lib/components/auth/RecoveryCodes.svelte";
  import OidcProviderButtons from "#lib/components/auth/OidcProviderButtons.svelte";
  import PasskeyRegistrationForm from "#lib/components/auth/PasskeyRegistrationForm.svelte";
  import CopyButton from "#lib/components/members/CopyButton.svelte";
  import AddressComposer from "#lib/components/tenants/AddressComposer.svelte";
  import AddressUses from "#lib/components/tenants/AddressUses.svelte";
  import {
    describePasskeyError,
    parseCeremonyOptions,
  } from "#lib/components/auth/passkey-errors.js";
  import { describeSubmitError } from "#lib/forms/index.js";
  import { retainQuery } from "#lib/api/retain-query.svelte.js";
  import { formatNumericDate } from "#lib/utils/formatting.js";
  import { createSlugAvailability } from "#lib/components/tenants/slug-availability.svelte.js";

  // ── Link ──────────────────────────────────────────────────────────
  const token = $derived(page.url.searchParams.get("token") ?? "");

  const infoQuery = $derived(token ? getInfo(token) : undefined);
  retainQuery(() => infoQuery);
  const info = $derived(infoQuery?.current);
  const baseDomain = $derived(info?.invite?.baseDomain ?? "");

  const linkError = $derived.by(() => {
    if (!token) return "The link has no invite code. Check the link that you got.";
    if (infoQuery?.error) {
      return describeSubmitError(
        infoQuery.error,
        "This invite link is not valid. Ask the person who sent it for a new link.",
      );
    }
    return null;
  });

  const oidcQuery = getOidcProviders();
  const oidc = $derived(oidcQuery.current);
  const hasOidc = $derived(oidc?.enabled && (oidc?.providers?.length ?? 0) > 0);

  // ── Steps ─────────────────────────────────────────────────────────
  const STEPS = [
    { id: "welcome", label: "Welcome", art: "welcome" },
    { id: "address", label: "Address", art: null },
    { id: "account", label: "Account", art: "account" },
    { id: "codes", label: "Recovery codes", art: null },
    { id: "done", label: "Your address", art: "done" },
  ] as const satisfies readonly { id: string; label: string; art: StepArt | null }[];
  type StepId = (typeof STEPS)[number]["id"];

  // A return from a sign-in provider carries the address already chosen.
  let step = $state<StepId>(page.url.searchParams.has("slug") ? "account" : "welcome");
  const stepIndex = $derived(STEPS.findIndex((s) => s.id === step));
  const current = $derived(STEPS[stepIndex]!);

  let siteName = $state(page.url.searchParams.get("name") ?? "");
  let slug = $state(page.url.searchParams.get("slug") ?? "");

  const availability = createSlugAvailability(
    () => slug,
    (value) => validateSlug({ token, slug: value }),
  );
  const shownSlug = $derived(availability.normalized || "name");
  const previewAddress = $derived(`https://${shownSlug}.${baseDomain}`);
  const addressReady = $derived(
    siteName.trim().length > 0 && availability.current.state === "available",
  );

  // ── Result ────────────────────────────────────────────────────────
  let siteAddress = $state<string | null>(null);
  let recoveryCodes = $state<string[]>([]);
  let accountError = $state<string | null>(null);
  let isWorking = $state(false);
  let signedInAfterRegistration = $state(false);

  const signedIn = $derived(signedInAfterRegistration || (info?.signedIn ?? false));

  let qrDataUrl = $state<string | null>(null);
  $effect(() => {
    const address = siteAddress;
    if (!address) return;
    let cancelled = false;
    void QRCode.toDataURL(address, { margin: 0, width: 264 }).then((url) => {
      if (!cancelled) qrDataUrl = url;
    });
    return () => {
      cancelled = true;
    };
  });

  async function createAsSignedIn() {
    isWorking = true;
    accountError = null;
    try {
      const result = await redeem({
        token,
        request: { slug: availability.normalized, siteName: siteName.trim() },
      });
      siteAddress = result.address ?? null;
      step = "done";
    } catch (err) {
      accountError = describeSubmitError(err, "Could not make your site. Try again.");
    } finally {
      isWorking = false;
    }
  }

  async function registerWithPasskey(username: string, displayName: string) {
    isWorking = true;
    accountError = null;
    try {
      const request = {
        token,
        slug: availability.normalized,
        siteName: siteName.trim(),
        username,
      };
      const options = await tenantInviteOptions({ ...request, displayName });
      const attestation = await startRegistration({
        optionsJSON: parseCeremonyOptions<PublicKeyCredentialCreationOptionsJSON>(options.options),
      });
      const result = await tenantInviteComplete({
        ...request,
        attestationResponseJson: JSON.stringify(attestation),
        challengeToken: options.challengeToken ?? "",
      });

      if (result.accessToken) {
        await setAuthCookies({
          accessToken: result.accessToken,
          refreshToken: result.refreshToken ?? undefined,
          expiresIn: result.expiresIn ?? undefined,
        });
      }

      signedInAfterRegistration = true;
      recoveryCodes = result.recoveryCodes ?? [];
      siteAddress = result.tenant?.address ?? null;
      if (!result.tenant) {
        accountError = `We made your account, but not your site. ${result.tenantRefusal ?? ""} Select another address, then try again.`;
      }
      step = "codes";
    } catch (err) {
      accountError = describePasskeyError(err, "register", "Could not make your account. Try again.");
    } finally {
      isWorking = false;
    }
  }

  let isRedirecting = $state(false);
  let selectedProvider = $state<string | null>(null);

  function returnHere(): string {
    const params = new URLSearchParams({ token, slug: availability.normalized, name: siteName.trim() });
    return `${resolve("/(unauthenticated)/start")}?${params.toString()}`;
  }

  function signInWithProvider(providerId: string) {
    isRedirecting = true;
    selectedProvider = providerId;
    const params = new URLSearchParams({ provider: providerId, returnUrl: returnHere() });
    window.location.href = `/api/auth/oidc/login?${params.toString()}`;
  }

  function next() {
    if (step === "welcome") step = "address";
    else if (step === "address" && addressReady) step = "account";
    else if (step === "done" && siteAddress) window.location.assign(`${siteAddress}/setup`);
  }

  function afterCodes() {
    step = siteAddress ? "done" : "address";
  }

  function back() {
    if (step === "address") step = "welcome";
    else if (step === "account") step = "address";
  }

  const canGoNext = $derived(
    step === "welcome" ||
      (step === "address" && addressReady) ||
      (step === "done" && !!siteAddress),
  );
  const canGoBack = $derived(step === "address" || (step === "account" && !isWorking));
  const nextLabel = $derived(
    step === "welcome" ? "Start" : step === "done" ? "Go to my site" : "Next",
  );
</script>

<svelte:head>
  <title>Make your Nocturne site</title>
</svelte:head>

<SetupChrome>
  {#snippet actions()}
    {#if baseDomain}
      <span class="hidden font-mono text-xs text-muted-foreground sm:inline">{baseDomain}</span>
    {/if}
  {/snippet}

  {#if linkError}
    <div class="mx-auto max-w-lg py-16 text-center">
      <div class="rounded-2xl border border-destructive/20 bg-destructive/5 p-8">
        <AlertTriangle class="mx-auto mb-4 h-10 w-10 text-destructive" />
        <h1 class="mb-2 text-xl font-semibold">You cannot use this link</h1>
        <p class="text-sm text-muted-foreground">{linkError}</p>
      </div>
    </div>
  {:else if !info}
    <div class="flex items-center justify-center py-24">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else}
    <div class="@container/wizard">
      <div
        class="mx-auto grid w-full max-w-280 grid-cols-1 items-start gap-6 @4xl/wizard:grid-cols-[220px_minmax(0,1fr)] @4xl/wizard:gap-11"
      >
        <aside class="@4xl/wizard:sticky @4xl/wizard:top-8">
          <h2 class="font-brand text-2xl font-hairline leading-tight tracking-tight">Your new site</h2>
          <p class="mt-1 mb-6 text-sm text-muted-foreground">About three minutes.</p>
          <ol class="relative flex gap-1 overflow-x-auto @4xl/wizard:flex-col @4xl/wizard:gap-0 @4xl/wizard:before:absolute @4xl/wizard:before:top-3.5 @4xl/wizard:before:bottom-3.5 @4xl/wizard:before:left-[11px] @4xl/wizard:before:w-px @4xl/wizard:before:bg-border">
            {#each STEPS as s, i (s.id)}
              <li class="relative flex items-center gap-3 py-2 text-sm {i === stepIndex ? 'font-semibold text-foreground' : 'text-muted-foreground'}" aria-current={i === stepIndex ? "step" : undefined}>
                <span
                  class="z-10 grid size-6 shrink-0 place-items-center rounded-full border bg-background text-xs font-semibold {i < stepIndex ? 'border-primary bg-primary text-primary-foreground' : i === stepIndex ? 'border-primary ring-4 ring-primary/10' : ''}"
                >
                  {#if i < stepIndex}<Check class="h-3 w-3" />{:else}{i + 1}{/if}
                </span>
                <span class="hidden @4xl/wizard:inline">{s.label}</span>
              </li>
            {/each}
          </ol>
        </aside>

        <section class="@container/card flex flex-col overflow-clip rounded-3xl border bg-card text-card-foreground shadow-sm @4xl/wizard:min-h-135">
          <div class="flex items-center justify-between gap-2.5 border-b px-4 py-3.5 @xl/card:px-7 @xl/card:py-5">
            <span class="flex items-center gap-3 text-xs text-muted-foreground">
              <span class="font-mono uppercase tracking-wide">Step {String(stepIndex + 1).padStart(2, "0")} / {String(STEPS.length).padStart(2, "0")}</span>
              <span aria-hidden="true">·</span>
              <span>{current.label}</span>
            </span>
            <Progress value={((stepIndex + 1) / STEPS.length) * 100} class="h-1 w-30" aria-label="Progress" />
          </div>

          <div class="@container flex-1 px-4 py-8 @xl/card:px-10">
            {#if current.art}
              <StepArtwork art={current.art} class="mx-auto mb-2 size-28 @xl/card:size-40" />
            {/if}

            {#if step === "welcome"}
              <div class="flex flex-col items-center gap-4 text-center">
                {#if info.invite?.invitedByName}
                  <span class="rounded-full border px-3 py-1 text-sm text-muted-foreground">{info.invite.invitedByName} invited you</span>
                {/if}
                <h1 class="max-w-[16ch] font-brand text-3xl font-hairline leading-tight tracking-tight text-balance @lg:text-5xl">
                  Make your own <em class="not-italic font-normal">Nocturne</em> site.
                </h1>
                <p class="max-w-prose text-base text-muted-foreground">
                  A site keeps the diabetes data of one person in one place: glucose, insulin, carbs, and the devices that send them.
                </p>
              </div>
              <dl class="mx-auto mt-8 max-w-lg divide-y border-y">
                <div class="grid grid-cols-[20px_1fr] gap-3.5 py-3.5">
                  <Globe class="mt-0.5 h-4.5 w-4.5 text-muted-foreground" />
                  <div><dt class="font-semibold">Your site gets its own address</dt><dd class="text-muted-foreground">You select it in the next step.</dd></div>
                </div>
                <div class="grid grid-cols-[20px_1fr] gap-3.5 py-3.5">
                  <Fingerprint class="mt-0.5 h-4.5 w-4.5 text-muted-foreground" />
                  <div><dt class="font-semibold">You sign in with a passkey</dt><dd class="text-muted-foreground">Your fingerprint, face, or device PIN. You do not need a password.</dd></div>
                </div>
                <div class="grid grid-cols-[20px_1fr] gap-3.5 py-3.5">
                  <Lock class="mt-0.5 h-4.5 w-4.5 text-muted-foreground" />
                  <div><dt class="font-semibold">You own the site</dt><dd class="text-muted-foreground">You decide who sees your data. The administrators of this server can open your site for support. Nocturne records each time.</dd></div>
                </div>
              </dl>
              {#if info.invite?.expiresAt}
                <p class="mt-4 text-center text-sm text-muted-foreground">
                  This link stops working on {formatNumericDate(new Date(info.invite.expiresAt))}.
                </p>
              {/if}
            {:else if step === "address"}
              <div class="flex flex-col items-center gap-3 text-center">
                <h1 class="font-brand text-3xl font-hairline leading-tight tracking-tight @lg:text-5xl">
                  Select your <em class="not-italic font-normal">address</em>.
                </h1>
                <p class="max-w-prose text-base text-muted-foreground">
                  Only your site uses this address. Select one that is easy to type on a phone.
                </p>
              </div>
              <form
                class="mx-auto mt-8 max-w-2xl"
                onsubmit={(e) => {
                  e.preventDefault();
                  next();
                }}
              >
                <AddressComposer bind:value={slug} {baseDomain} availability={availability.current} />
                <div class="mt-9">
                  <AddressUses slug={shownSlug} {baseDomain} />
                </div>
                <div class="mx-auto mt-7 max-w-sm space-y-2">
                  <Label for="site-name">Site name</Label>
                  <Input id="site-name" bind:value={siteName} maxlength={256} placeholder="For example, Sam's glucose" />
                  <p class="text-sm text-muted-foreground">Nocturne shows this name at the top of your site. You can change it later.</p>
                </div>
              </form>
            {:else if step === "account"}
              <div class="flex flex-col items-center gap-3 text-center">
                <h1 class="font-brand text-3xl font-hairline leading-tight tracking-tight @lg:text-5xl">
                  {#if signedIn}Make your <em class="not-italic font-normal">site</em>.{:else}Make your <em class="not-italic font-normal">passkey</em>.{/if}
                </h1>
                <p class="max-w-prose text-base text-muted-foreground">
                  {#if signedIn}You will own the new site.{:else}Your device keeps the passkey. You use it each time you sign in to your site.{/if}
                </p>
                <span class="inline-flex items-center gap-2 rounded-full bg-muted px-3 py-1.5 font-mono text-xs">
                  <Globe class="h-3.5 w-3.5" />
                  {previewAddress}
                </span>
              </div>

              <div class="mx-auto mt-7 grid max-w-sm gap-4">
                {#if accountError}
                  <p class="text-sm text-destructive" role="alert">{accountError}</p>
                {/if}

                {#if signedIn}
                  {#if info.viewerName}
                    <p class="text-center text-sm">You are signed in as <strong>{info.viewerName}</strong>.</p>
                  {/if}
                  <Button size="lg" disabled={isWorking || availability.current.state !== "available"} onclick={createAsSignedIn}>
                    {#if isWorking}<Loader2 class="h-4 w-4 animate-spin" />{/if}
                    Make my site
                  </Button>
                {:else}
                  <PasskeyRegistrationForm
                    onRegister={registerWithPasskey}
                    disabled={isRedirecting || availability.current.state !== "available"}
                    isRegistering={isWorking}
                  />
                  {#if hasOidc && oidc}
                    <OidcProviderButtons
                      providers={oidc.providers}
                      disabled={isRedirecting || isWorking}
                      onLogin={signInWithProvider}
                      {isRedirecting}
                      {selectedProvider}
                    />
                  {/if}
                  <p class="text-center text-sm text-muted-foreground">
                    Do you have an account on this server? Sign in at your own site, then open this link again.
                  </p>
                {/if}
              </div>
            {:else if step === "codes"}
              <div class="flex flex-col items-center gap-3 text-center">
                <h1 class="font-brand text-3xl font-hairline leading-tight tracking-tight @lg:text-5xl">
                  Save your <em class="not-italic font-normal">recovery codes</em>.
                </h1>
                <p class="max-w-prose text-base text-muted-foreground">
                  If you lose your passkey, one code lets you sign in again. Each code works one time.
                </p>
              </div>
              <div class="mx-auto mt-7 max-w-md space-y-5">
                {#if accountError}
                  <p class="text-sm text-destructive" role="alert">{accountError}</p>
                {/if}
                <RecoveryCodes codes={recoveryCodes} onContinue={afterCodes} continueLabel="Next" />
              </div>
            {:else if step === "done" && siteAddress}
              <div class="flex flex-col items-center gap-3 text-center">
                <h1 class="font-brand text-3xl font-hairline leading-tight tracking-tight @lg:text-5xl">
                  Your site is <em class="not-italic font-normal">ready</em>.
                </h1>
                <p class="max-w-prose text-base text-muted-foreground">
                  Keep this address. You use it to sign in, in your apps, and with your followers.
                </p>
              </div>
              <div class="mx-auto mt-7 grid max-w-2xl overflow-hidden rounded-2xl border @xl:grid-cols-[1fr_auto]">
                <div class="flex flex-col gap-3 p-6">
                  <span class="text-xs text-muted-foreground">Your site address</span>
                  <span class="font-brand text-2xl font-light tracking-tight break-all">{siteAddress}</span>
                  <div><CopyButton text={siteAddress} label="Copy your site address" /></div>
                  <ul class="mt-1 grid gap-2 text-sm">
                    <li class="flex gap-2.5"><Bookmark class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />Add a bookmark for it on each device.</li>
                    <li class="flex gap-2.5"><Smartphone class="mt-0.5 h-4 w-4 shrink-0 text-muted-foreground" />Use this address, not {baseDomain}, in your apps.</li>
                  </ul>
                </div>
                {#if qrDataUrl}
                  <div class="flex flex-col items-center gap-2 border-t border-dashed bg-muted/60 p-5 @xl:border-t-0 @xl:border-l">
                    <img src={qrDataUrl} alt="QR code of your site address" class="size-33 rounded-sm bg-white p-1.5" />
                    <p class="max-w-36 text-center text-xs text-muted-foreground">Scan with your phone to open your site there.</p>
                  </div>
                {/if}
              </div>
              <p class="mt-5 text-center text-muted-foreground">Next, Nocturne helps you connect your devices and apps.</p>
            {/if}
          </div>

          {#if step !== "account" && step !== "codes"}
            <div class="sticky bottom-0 z-10 flex items-center justify-between gap-3 border-t bg-background px-4 py-3.5 @xl/card:px-7 @4xl/wizard:static @4xl/wizard:bg-muted/40">
              <div>
                {#if canGoBack}
                  <Button variant="outline" onclick={back}><ArrowLeft class="h-4 w-4" />Back</Button>
                {/if}
              </div>
              <Button onclick={next} disabled={!canGoNext}>
                {nextLabel}
                <ArrowRight class="h-4 w-4" />
              </Button>
            </div>
          {:else if step === "account" && canGoBack}
            <div class="border-t px-4 py-3.5 @xl/card:px-7 @4xl/wizard:bg-muted/40">
              <Button variant="outline" onclick={back}><ArrowLeft class="h-4 w-4" />Change the address</Button>
            </div>
          {/if}
        </section>
      </div>
    </div>
  {/if}
</SetupChrome>
