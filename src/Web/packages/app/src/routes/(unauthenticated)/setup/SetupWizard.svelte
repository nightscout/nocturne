<script lang="ts">
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import { tick } from "svelte";
  import ArrowRight from "@lucide/svelte/icons/arrow-right";
  import ArrowLeft from "@lucide/svelte/icons/arrow-left";
  import Sprout from "@lucide/svelte/icons/sprout";
  import Cable from "@lucide/svelte/icons/cable";
  import ShieldAlert from "@lucide/svelte/icons/shield-alert";
  import { Button } from "$lib/components/ui/button";
  import { markSetupComplete } from "./setup.remote";
  import { Badge } from "$lib/components/ui/badge";
  import { Progress } from "$lib/components/ui/progress";
  import {
    getServicesOverview,
    getActiveDataSources,
    getUploaderSetup,
  } from "$api/generated/services.generated.remote";
  import {
    getPatientRelationship,
    setPatientRelationship,
    getUnitsAndTimezone,
    setUnitsAndTimezone,
  } from "$api/generated/tenantSettings.generated.remote";
  import { applyPreferences, type GlucoseUnits } from "$lib/stores/appearance-store.svelte";
  import { isGlucoseUnits } from "$lib/utils/formatting";
  import { patientVoice } from "$lib/onboarding/patient-voice.svelte";
  import { startOrResumeMigration } from "./migration-session";
  import { describeSubmitError, errorStatus } from "$lib/forms/submit-error";
  import type {
    UploaderApp,
    DataSourceInfo,
    PatientRelationship,
  } from "$lib/api/generated/nocturne-api-client";

  import StepSidebar from "./StepSidebar.svelte";
  import SetupChrome from "./SetupChrome.svelte";
  import TenantIdentity from "./steps/TenantIdentity.svelte";
  import AccountCreation from "./steps/AccountCreation.svelte";
  import WhoFor from "./steps/WhoFor.svelte";
  import UnitsAndTimezone from "./steps/UnitsAndTimezone.svelte";
  import PathChoice from "./steps/PathChoice.svelte";
  import NightscoutConnect from "./steps/NightscoutConnect.svelte";
  import DataSourceSelectionView from "$lib/components/connectors/DataSourceSelectionView.svelte";
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";
  import UploaderSetupView from "$lib/components/connectors/UploaderSetupView.svelte";
  import ImportProgress from "./steps/ImportProgress.svelte";
  import Finish, {
    type ImportResult,
    type SourceResult,
  } from "./steps/Finish.svelte";
  import StepArtwork, { type StepArt } from "./StepArtwork.svelte";
  import { retainQuery } from "$lib/api/retain-query.svelte";

  // Auth check is handled server-side in +page.server.ts:
  // - setupRequired=true → show two-step setup (tenant identity → account creation)
  // - Subjects exist but not authenticated → redirect to /auth/login
  // - Authenticated → show onboarding wizard

  // ── HTTPS guard ─────────────────────────────────────────────────────
  const httpsRequired = $derived(
    browser &&
      window.location.protocol !== "https:" &&
      !window.location.hostname.match(/^(localhost|127\.0\.0\.1|::1|\[::1\])$/)
  );

  // ── Setup phase (pre-auth) ──────────────────────────────────────────
  let accountCreated = $state(false);
  const setupRequired = $derived(
    !accountCreated && page.data?.setupRequired === true
  );

  type StepDef = { id: string; label: string; short: string; art: StepArt };

  const SETUP_STEPS = [
    { id: "tenant", label: "Name your instance", short: "Instance", art: "welcome" },
    { id: "account", label: "Create your account", short: "Account", art: "account" },
  ] as const satisfies readonly StepDef[];

  // If the tenant already exists (user abandoned after step 1), skip to account creation.
  // page.data is resolved before render, so this is correct at init time.
  let setupStepIndex = $state(page.data?.tenantExists === true ? 1 : 0);

  function handleTenantCreated(_slug: string) {
    setupStepIndex = 1;
  }

  function handleAccountCreated() {
    // Flip client-side — no server round-trip needed. The user just
    // registered; we already have auth cookies. Trying to reload or goto
    // the same URL hits a redirect loop because +page.server.ts falls
    // through to the /auth/login redirect before hooks recognise the
    // freshly-set session.
    // Reactive queries auto-activate when setupRequired becomes false.
    accountCreated = true;
  }

  // ── State ───────────────────────────────────────────────────────────
  let path = $state<"fresh" | "migration">("fresh");
  let stepIndex = $state(0);
  let selectedConnectorId = $state<string | null>(null);
  let selectedUploader = $state<UploaderApp | null>(null);
  // ── Reactive service queries (auto-activate when setup completes) ──
  // query() returns reactive objects anchored to this component's lifecycle.
  // They cannot be awaited in event handlers — must live in $derived context.
  const servicesQuery = $derived(!setupRequired ? getServicesOverview() : null);
  const dataSourcesQuery = $derived(
    !setupRequired ? getActiveDataSources() : null
  );
  const relationshipQuery = $derived(
    !setupRequired ? getPatientRelationship() : null
  );
  const uploaderSetupQuery = $derived(
    selectedUploader?.id ? getUploaderSetup(selectedUploader.id) : null
  );
  retainQuery(() => servicesQuery);
  retainQuery(() => dataSourcesQuery);
  retainQuery(() => uploaderSetupQuery);
  retainQuery(() => relationshipQuery);

  const servicesData = $derived(servicesQuery?.current ?? null);
  const activeDataSources = $derived<DataSourceInfo[]>(
    dataSourcesQuery?.current ?? []
  );
  const uploaderSetupResponse = $derived(uploaderSetupQuery?.current ?? null);
  const servicesLoading = $derived(
    servicesQuery == null ||
      servicesQuery.current === undefined ||
      dataSourcesQuery == null ||
      dataSourcesQuery.current === undefined
  );
  let importProgress = $state(0);
  let importResult = $state<ImportResult>(null);
  // The import step blocks until the run ends or its status can no longer be followed.
  let importSettled = $state(false);
  let sourceResult = $state<SourceResult>(null);
  let migrationJobId = $state<string | undefined>(undefined);
  let migrationStartError = $state<string | undefined>(undefined);
  // The import starts after the units step, from the Nightscout connection saved before it.
  let nightscoutConnected = $state(false);
  let navigating = false;

  // Until the step is answered here, it shows the tenant's stored answer.
  let chosenRelationship = $state<PatientRelationship | undefined>(undefined);
  let chosenName = $state<string | undefined>(undefined);
  let relationshipError = $state<string | undefined>(undefined);
  const relationship = $derived(
    chosenRelationship ?? relationshipQuery?.current?.relationship
  );
  const patientName = $derived(
    chosenName ?? relationshipQuery?.current?.patientName ?? ""
  );
  const voice = $derived(patientVoice({ relationship, patientName }));

  // ── Onboarding step definitions (post-auth) ────────────────────────
  const importLabel = $derived(
    voice.kind === "self"
      ? "Import your history"
      : voice.kind === "named"
        ? `Import ${voice.name}'s history`
        : "Import the history"
  );

  const STEPS = $derived({
    fresh: [
      { id: "who", label: "Who it's for", short: "Who", art: "who" },
      { id: "path", label: "Choose your path", short: "Path", art: "welcome" },
      { id: "units", label: "Units and timezone", short: "Units", art: "units" },
      { id: "cgm", label: "Connect a data source", short: "Source", art: "source" },
      { id: "sync", label: "Configure & sync", short: "Setup", art: "source" },
      { id: "finish", label: "Finish", short: "Done", art: "done" },
    ],
    migration: [
      { id: "who", label: "Who it's for", short: "Who", art: "who" },
      { id: "path", label: "Choose your path", short: "Path", art: "welcome" },
      { id: "connect", label: "Connect Nightscout", short: "Connect", art: "source" },
      { id: "units", label: "Units and timezone", short: "Units", art: "units" },
      { id: "import", label: importLabel, short: "Import", art: "import" },
      { id: "finish", label: "Finish", short: "Done", art: "done" },
    ],
  } as const satisfies Record<string, readonly StepDef[]>);

  const steps = $derived(STEPS[path]);
  const currentStep = $derived(steps[stepIndex]);

  // Read on the step itself, so a Nightscout saved just before it is asked for its settings.
  const unitsQuery = $derived(
    !setupRequired && currentStep?.id === "units"
      ? getUnitsAndTimezone({
          locale: browser ? navigator.language : undefined,
          fromNightscout: path === "migration" && nightscoutConnected,
        })
      : null
  );
  retainQuery(() => unitsQuery);
  const unitsAnswer = $derived(unitsQuery?.current);
  const browserTimezone = browser
    ? Intl.DateTimeFormat().resolvedOptions().timeZone
    : "";
  let chosenUnits = $state<GlucoseUnits | undefined>(undefined);
  let chosenTimezone = $state<string | undefined>(undefined);
  let unitsError = $state<string | undefined>(undefined);
  const units = $derived(
    chosenUnits ??
      (isGlucoseUnits(unitsAnswer?.glucoseUnits) ? unitsAnswer.glucoseUnits : undefined)
  );
  const timezone = $derived(
    chosenTimezone ?? unitsAnswer?.timezone ?? browserTimezone
  );

  // Both phases share one layout; these pick the phase's steps.
  const activeSteps: readonly StepDef[] = $derived(
    setupRequired ? SETUP_STEPS : steps
  );
  const activeIndex = $derived(setupRequired ? setupStepIndex : stepIndex);
  const activeStep = $derived(activeSteps[activeIndex]);
  const progressPct = $derived(
    activeSteps.length <= 1
      ? 100
      : (activeIndex / (activeSteps.length - 1)) * 100
  );

  let stepCard = $state<HTMLElement>();
  let shownStepId: string | undefined;

  // The steps share one card, so a step change keeps the scroll position and the focused
  // button. Bring the card back into view and land keyboard and screen-reader users on the new
  // heading.
  $effect(() => {
    const id = activeStep?.id;
    const previous = shownStepId;
    shownStepId = id;
    if (previous === undefined || previous === id) return;
    void tick().then(() => {
      if (!stepCard) return;
      if (stepCard.getBoundingClientRect().top < 0) {
        const reduceMotion = matchMedia("(prefers-reduced-motion: reduce)").matches;
        stepCard.scrollIntoView({ block: "start", behavior: reduceMotion ? "auto" : "smooth" });
      }
      const heading = stepCard.querySelector<HTMLElement>("h1");
      if (heading) {
        heading.tabIndex = -1;
        heading.focus({ preventScroll: true });
      }
    });
  });

  const importBlocking = $derived(
    activeStep?.id === "import" && !importSettled
  );

  function handleJumpToStep(index: number) {
    if (importBlocking) return;
    if (!setupRequired) void navigateTo(index);
    else if (index <= setupStepIndex) setupStepIndex = index;
  }

  const artProgress = $derived(
    currentStep?.id === "import" ? importProgress / 100 : undefined
  );

  // ── Data source loading ──────────────────────────────────────────────
  const connectors = $derived(servicesData?.availableConnectors ?? []);
  const uploaderApps = $derived(servicesData?.uploaderApps ?? []);

  // ── Navigation ──────────────────────────────────────────────────────
  // A fresh ImportProgress settles again on its own, so leaving the step forgets it.
  function goToStep(index: number) {
    stepIndex = index;
    importSettled = false;
  }

  function handleBack() {
    if (stepIndex > 0) goToStep(stepIndex - 1);
  }

  function handleNext() {
    if (stepIndex < steps.length - 1) return navigateTo(stepIndex + 1);
  }

  /**
   * Moves forward or back, starting the Nightscout import on the way into its step by any route.
   * A forward move from before the import lands on it rather than past it, so the import is
   * always started and watched before the finish.
   */
  async function navigateTo(index: number) {
    if (navigating) return;
    const importIndex = steps.findIndex((s) => s.id === "import");
    if (importIndex < 0 || index <= stepIndex || stepIndex >= importIndex || index < importIndex) {
      goToStep(index);
      return;
    }

    navigating = true;
    try {
      await startImport();
      goToStep(importIndex);
    } finally {
      navigating = false;
    }
  }

  function handleWhoForSkip() {
    chosenRelationship = undefined;
    chosenName = undefined;
    relationshipError = undefined;
    handleNext();
  }

  // Saves only an answer that differs from the stored one. The server's reply replaces the local
  // edit, since a cleared name leaves the stored one in place.
  async function handleWhoForContinue() {
    const stored = relationshipQuery?.current;
    const changed =
      relationship !== stored?.relationship ||
      patientName.trim() !== (stored?.patientName ?? "");
    if (relationship && changed) {
      try {
        relationshipError = undefined;
        const saved = await setPatientRelationship({
          relationship,
          patientName: patientName.trim() || undefined,
        });
        chosenRelationship = saved.relationship;
        chosenName = saved.patientName ?? "";
      } catch (err) {
        relationshipError = describeSubmitError(err, "We couldn't save your answer.");
        return;
      }
    }
    handleNext();
  }

  let unitsSaving = $state(false);

  // Skip saves too: the step shows a checked unit, and leaving without saving would put the app
  // in mg/dL whatever was shown. Only a step that never loaded an answer leaves without one.
  async function handleUnitsLeave() {
    if (unitsSaving) return;
    unitsSaving = true;
    try {
      if (units && timezone) {
        unitsError = undefined;
        await setUnitsAndTimezone({ glucoseUnits: units, timezone });
        applyPreferences({ glucoseUnits: units }, { refreshCookie: true });
      }
      await handleNext();
    } catch (err) {
      unitsError = describeSubmitError(err, "We couldn't save your units and timezone.");
    } finally {
      unitsSaving = false;
    }
  }

  async function handleExitSetup() {
    await markSetupComplete();
    await goto(resolve("/"), { invalidateAll: true });
  }

  // The core ends on the setup hub, which /setup serves only once the server has recorded the
  // core as complete; until then /setup is still the core, so a failure stays here to be retried.
  let continueFailed = $state(false);
  async function handleContinueToHub() {
    continueFailed = false;
    const result = await markSetupComplete().catch(() => undefined);
    if (!result?.completed) {
      continueFailed = true;
      return;
    }
    await goto(resolve("/setup"), { invalidateAll: true });
  }

  async function handleTakeTour() {
    await markSetupComplete();
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- the resolved dashboard route plus a ?coach= param
    await goto(`${resolve("/")}?coach=quick-tour`, { invalidateAll: true });
  }

  function handleSelectConnector(id: string) {
    selectedConnectorId = id;
    selectedUploader = null;
    handleNext();
  }

  function handleSelectUploader(app: UploaderApp) {
    selectedUploader = app;
    selectedConnectorId = null;
    // uploaderSetupQuery auto-fetches reactively when selectedUploader.id changes
    handleNext();
  }

  function goToFinish() {
    const finishIdx = steps.findIndex((s) => s.id === "finish");
    if (finishIdx >= 0) goToStep(finishIdx);
  }

  function handleConnectorSaved() {
    sourceResult = "connector-saved";
    goToFinish();
  }

  function handleUploaderReceiving() {
    sourceResult = "uploader-receiving";
    goToFinish();
  }

  // The Nightscout history import is started from the saved connector. It lives here (a
  // deliberate action after the user connects their source) rather than auto-firing inside the
  // progress view, so it runs in the onboarding tenant's own request context.
  const MIGRATION_CONNECTOR = "nightscout";

  // startOrResumeMigration reuses a job still in flight or one this session completed, so a
  // failed job is replaced rather than watched again.
  async function startImport() {
    if (path !== "migration" || !nightscoutConnected) return;
    try {
      migrationStartError = undefined;
      migrationJobId = await startOrResumeMigration(MIGRATION_CONNECTOR);
    } catch (err) {
      // Every 400 from start-from-connector means the saved connector is missing or has no URL.
      migrationStartError =
        errorStatus(err) === 400
          ? "Your Nightscout connection isn't saved yet."
          : describeSubmitError(err, "Something went wrong while starting it.");
    }
  }
</script>

<svelte:head>
  <title>Get Started - Nocturne</title>
</svelte:head>

<SetupChrome>
  {#snippet actions()}
    <span class="hidden font-mono text-xs text-muted-foreground sm:inline">
      Step {activeIndex + 1} of {activeSteps.length}
    </span>

    {#if !setupRequired}
      <!-- Marks onboarding complete on the server so it is not offered again. -->
      <Button variant="ghost-muted" size="xs" onclick={handleExitSetup}>
        Exit setup
      </Button>
    {/if}
  {/snippet}

  {#if httpsRequired}
    <div class="w-full max-w-lg mx-auto text-center py-20">
      <div
        class="rounded-2xl border border-destructive/20 bg-destructive/5 p-8"
      >
        <ShieldAlert class="mx-auto mb-4 h-12 w-12 text-destructive" />
        <h2 class="text-xl font-semibold mb-3">HTTPS Required</h2>
        <p class="text-muted-foreground text-sm leading-relaxed">
          Nocturne requires a secure connection. Please access this site using <strong
            class="text-foreground"
          >
            https://
          </strong>
          instead of http://.
        </p>
        <p class="text-muted-foreground text-xs mt-4">
          Passkey authentication and secure cookies require HTTPS to function.
        </p>
      </div>
    </div>
  {:else}
    <div
      class="w-full max-w-280 mx-auto grid grid-cols-1 gap-6 lg:grid-cols-[240px_minmax(0,1fr)] lg:gap-10 xl:grid-cols-[320px_minmax(0,1fr)] xl:gap-14 items-start"
    >
      <aside class="lg:sticky lg:top-8">
        <StepSidebar
          path={setupRequired ? "fresh" : path}
          currentStep={activeIndex}
          steps={activeSteps}
          {voice}
          onJumpToStep={handleJumpToStep}
        />
      </aside>

      <section
        bind:this={stepCard}
        class="relative rounded-3xl border bg-card text-card-foreground shadow-sm overflow-clip lg:min-h-135 flex flex-col"
      >
        <div
          class="flex items-center justify-between flex-wrap gap-2.5 px-5 py-3.5 md:px-7 md:py-5 border-b"
        >
          <div class="flex items-center gap-3 text-xs text-muted-foreground">
            <span class="font-mono uppercase tracking-wide">
              Step {String(activeIndex + 1).padStart(2, "0")} / {String(
                activeSteps.length
              ).padStart(2, "0")}
            </span>
            <span aria-hidden="true">&middot;</span>
            <span>{activeStep?.short}</span>
          </div>
          <div class="flex items-center gap-3">
            {#if !setupRequired}
              <Badge variant="secondary">
                {#if path === "migration"}
                  <Cable />
                  Nightscout Migration
                {:else}
                  <Sprout />
                  Fresh Start
                {/if}
              </Badge>
            {/if}
            <Progress
              value={progressPct}
              class="h-1 w-30"
              aria-label="Setup progress"
            />
          </div>
        </div>

        <div class="flex-1 px-4 py-3 md:px-5 max-lg:[&_:is(input,select,textarea,[role=combobox],[role=radio])]:scroll-mb-32">
          <StepArtwork
            art={activeStep?.art ?? "welcome"}
            progress={artProgress}
            class="mx-auto mt-4 size-28 md:size-40 lg:size-48"
          />
          {#if activeStep?.id === "tenant"}
            <TenantIdentity onComplete={handleTenantCreated} />
          {:else if activeStep?.id === "account"}
            <AccountCreation onComplete={handleAccountCreated} />
          {:else if activeStep?.id === "who"}
            <WhoFor
              bind:relationship={
                () => relationship, (value) => (chosenRelationship = value)
              }
              bind:patientName={
                () => patientName, (value) => (chosenName = value)
              }
              error={relationshipError}
            />
          {:else if activeStep?.id === "path"}
            <PathChoice bind:path {voice} />
          {:else if activeStep?.id === "units"}
            <UnitsAndTimezone
              bind:units={() => units, (value) => (chosenUnits = value)}
              bind:timezone={
                () => timezone, (value) => (chosenTimezone = value)
              }
              timezoneDetected={chosenTimezone === undefined &&
                !unitsAnswer?.timezone}
              nightscout={unitsAnswer?.nightscout}
              nightscoutUnavailable={unitsAnswer?.nightscoutUnavailable}
              {voice}
              error={unitsError}
            />
          {:else if activeStep?.id === "connect"}
            <NightscoutConnect
              onComplete={() => {
                nightscoutConnected = true;
                migrationStartError = undefined;
                handleNext();
              }}
            />
          {:else if activeStep?.id === "cgm"}
            <div class="flex flex-col gap-8 px-4 py-8">
              <div class="flex flex-col items-center gap-4 text-center">
                <h1
                  class="font-brand font-hairline leading-tight tracking-tight text-3xl md:text-4xl xl:text-5xl"
                >
                  Connect a <em class="not-italic font-light text-primary">data source</em>.
                </h1>
                <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
                  {#if voice.kind === "self"}
                    Choose a cloud service or phone app to start sending your
                    glucose and treatment data to Nocturne. You can connect more later.
                  {:else if voice.kind === "named"}
                    Choose a cloud service or phone app to start sending {voice.name}'s
                    glucose and treatment data to Nocturne. You can connect more later.
                  {:else}
                    Choose a cloud service or phone app to start sending the glucose
                    and treatment data to Nocturne. You can connect more later.
                  {/if}
                </p>
              </div>
              <DataSourceSelectionView
                {connectors}
                {uploaderApps}
                dataSources={activeDataSources}
                isLoading={servicesLoading}
                loadError={null}
                onSelectConnector={handleSelectConnector}
                onSelectUploader={handleSelectUploader}
                onSkip={goToFinish}
              />
            </div>
          {:else if activeStep?.id === "sync"}
            <div class="flex flex-col gap-8 px-4 py-8">
              {#if selectedConnectorId}
                <div class="flex flex-col items-center gap-4 text-center">
                  <h1
                    class="font-brand font-hairline leading-tight tracking-tight text-3xl md:text-4xl xl:text-5xl"
                  >
                    Configure your <em class="not-italic font-light text-primary">connection</em>.
                  </h1>
                  <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
                    {#if voice.kind === "self"}
                      Enter your credentials and we'll start syncing your data.
                    {:else if voice.kind === "named"}
                      Enter the service's sign-in details and we'll start syncing
                      {voice.name}'s data.
                    {:else}
                      Enter the service's sign-in details and we'll start syncing the
                      data.
                    {/if}
                  </p>
                </div>
                <ConnectorSetup
                  connectorId={selectedConnectorId}
                  primaryAction="save-and-finish"
                  showToggle={false}
                  showDangerZone={false}
                  showCapabilities={true}
                  onComplete={handleConnectorSaved}
                  onCancel={handleBack}
                />
              {:else if selectedUploader}
                <div class="flex flex-col items-center gap-4 text-center">
                  <h1
                    class="font-brand font-hairline leading-tight tracking-tight text-3xl md:text-4xl xl:text-5xl"
                  >
                    Set up the <em class="not-italic font-light text-primary">app</em>.
                  </h1>
                  <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
                    Follow the steps below to connect the phone app to
                    Nocturne.
                  </p>
                </div>
                <UploaderSetupView
                  app={selectedUploader}
                  setupResponse={uploaderSetupResponse}
                  onBack={handleBack}
                  onConnected={handleUploaderReceiving}
                />
              {:else}
                <p class="px-4 py-8 text-center text-muted-foreground">
                  No data source selected. Go back to choose one.
                </p>
              {/if}
            </div>
          {:else if activeStep?.id === "import"}
            <ImportProgress
              jobId={migrationJobId}
              startError={migrationStartError}
              {voice}
              onProgressChange={(pct) => (importProgress = pct)}
              onResult={(result) => (importResult = result)}
              onSettled={() => (importSettled = true)}
              onComplete={goToFinish}
            />
          {:else if activeStep?.id === "finish"}
            <Finish
              {path}
              source={sourceResult}
              {importResult}
              {voice}
              onContinue={handleContinueToHub}
              {continueFailed}
              onTakeTour={handleTakeTour}
            />
          {/if}
        </div>

        {#if !setupRequired}
          <div
            class="flex flex-col-reverse gap-3 sm:flex-row sm:justify-between sm:items-center px-5 py-3.5 md:px-7 md:py-4.5 border-t bg-background lg:bg-muted/40 sticky bottom-0 z-10 lg:static *:sm:w-auto *:w-full"
          >
            <div class="empty:hidden [&>button]:w-full sm:[&>button]:w-auto">
              {#if stepIndex > 0 && currentStep?.id !== "finish" && !importBlocking}
                <Button variant="outline" onclick={handleBack}>
                  <ArrowLeft class="h-4 w-4" />
                  Back
                </Button>
              {/if}
            </div>
            <div class="flex flex-col-reverse gap-3 sm:flex-row sm:items-center *:w-full *:sm:w-auto">
              {#if currentStep?.id === "finish"}
                <Button onclick={handleContinueToHub}>
                  Continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {:else if currentStep?.id === "import"}
                {#if importSettled}
                  <Button onclick={handleNext}>
                    Continue
                    <ArrowRight class="h-4 w-4" />
                  </Button>
                {/if}
              {:else if currentStep?.id === "who"}
                <Button variant="ghost" onclick={handleWhoForSkip}>
                  Skip for now
                </Button>
                <Button onclick={handleWhoForContinue}>
                  Continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {:else if currentStep?.id === "units"}
                <Button variant="ghost" onclick={handleUnitsLeave} disabled={unitsSaving}>
                  Skip for now
                </Button>
                <Button
                  onclick={handleUnitsLeave}
                  disabled={unitsSaving || !units || !timezone}
                >
                  Continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {:else if currentStep?.id === "path"}
                <Button onclick={handleNext}>
                  Continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {:else if currentStep?.id !== "cgm"}
                <Button variant="ghost" onclick={handleNext}>
                  Skip for now
                </Button>
                {#if currentStep?.id !== "sync" && currentStep?.id !== "connect"}
                  <Button onclick={handleNext}>
                    Continue
                    <ArrowRight class="h-4 w-4" />
                  </Button>
                {/if}
              {/if}
            </div>
          </div>
        {/if}
      </section>
    </div>
  {/if}
</SetupChrome>
