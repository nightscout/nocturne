<script lang="ts">
  import { browser } from "$app/environment";
  import { goto } from "$app/navigation";
  import { resolve } from "$app/paths";
  import { page } from "$app/state";
  import {
    ArrowRight,
    ArrowLeft,
    Sprout,
    Cable,
    ShieldAlert,
  } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import { markSetupComplete } from "./setup.remote";
  import AppLogo from "$lib/components/ui/AppLogo.svelte";
  import { Badge } from "$lib/components/ui/badge";
  import { Progress } from "$lib/components/ui/progress";
  import {
    getServicesOverview,
    getActiveDataSources,
    getUploaderSetup,
  } from "$api/generated/services.generated.remote";
  import { startOrResumeMigration } from "./migration-session";
  import type {
    UploaderApp,
    DataSourceInfo,
  } from "$lib/api/generated/nocturne-api-client";

  import StepSidebar from "./StepSidebar.svelte";
  import TenantIdentity from "./steps/TenantIdentity.svelte";
  import AccountCreation from "./steps/AccountCreation.svelte";
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
  import type { StepArt } from "./StepArtwork.svelte";
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

  // ── Onboarding step definitions (post-auth) ────────────────────────
  const STEPS = {
    fresh: [
      { id: "path", label: "Choose your path", short: "Path", art: "welcome" },
      { id: "cgm", label: "Connect a data source", short: "Source", art: "source" },
      { id: "sync", label: "Configure & sync", short: "Setup", art: "source" },
      { id: "finish", label: "Finish", short: "Done", art: "done" },
    ],
    migration: [
      { id: "path", label: "Choose your path", short: "Path", art: "welcome" },
      { id: "connect", label: "Connect your Nightscout", short: "Connect", art: "source" },
      { id: "import", label: "Import your history", short: "Import", art: "import" },
      { id: "finish", label: "Finish", short: "Done", art: "done" },
    ],
  } as const satisfies Record<string, readonly StepDef[]>;

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
  const uploaderSetupQuery = $derived(
    selectedUploader?.id ? getUploaderSetup(selectedUploader.id) : null
  );
  retainQuery(() => servicesQuery);
  retainQuery(() => dataSourcesQuery);
  retainQuery(() => uploaderSetupQuery);

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
  let sourceResult = $state<SourceResult>(null);
  let migrationJobId = $state<string | undefined>(undefined);

  const steps = $derived(STEPS[path]);
  const currentStep = $derived(steps[stepIndex]);

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

  function handleJumpToStep(index: number) {
    if (!setupRequired) stepIndex = index;
    else if (index <= setupStepIndex) setupStepIndex = index;
  }

  const artProgress = $derived(
    currentStep?.id === "import" ? importProgress / 100 : undefined
  );

  // ── Data source loading ──────────────────────────────────────────────
  const connectors = $derived(servicesData?.availableConnectors ?? []);
  const uploaderApps = $derived(servicesData?.uploaderApps ?? []);

  // ── Navigation ──────────────────────────────────────────────────────
  function handleBack() {
    if (stepIndex > 0) stepIndex--;
  }

  function handleNext() {
    if (stepIndex < steps.length - 1) stepIndex++;
  }

  async function handleEnterDashboard() {
    await markSetupComplete();
    await goto(resolve("/"), { invalidateAll: true });
  }

  async function handleNavigateWithCoach(url: string) {
    await markSetupComplete();
    // eslint-disable-next-line svelte/no-navigation-without-resolve -- url is one of Finish.svelte's literal in-app paths with a ?coach= param
    await goto(url, { invalidateAll: true });
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
    if (finishIdx >= 0) stepIndex = finishIdx;
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

  async function handleMigrationConnected() {
    try {
      migrationJobId = await startOrResumeMigration(MIGRATION_CONNECTOR);
    } catch {
      // Leave migrationJobId unset; the import step shows a neutral state if no job exists.
    }
    handleNext();
  }

  // User info
  const userEmail = $derived(page.data?.user?.email ?? "");
  const userInitials = $derived(
    userEmail ? userEmail.slice(0, 2).toUpperCase() : "U"
  );
</script>

<svelte:head>
  <title>Get Started - Nocturne</title>
</svelte:head>

<div
  class="relative min-h-screen grid grid-rows-[auto_1fr_auto] bg-background text-foreground"
>
  <header
    class="relative z-50 flex items-center justify-between px-8 py-5.5 border-b bg-background/80 backdrop-blur-md max-[900px]:px-5 max-[900px]:py-3.5"
  >
    <div class="flex items-center gap-3">
      <AppLogo icon="nocturne" class="h-7 w-7" />
      <span class="font-brand text-xl font-light tracking-wide">nocturne</span>
    </div>

    <div class="flex items-center gap-5">
      <span class="hidden font-mono text-xs text-muted-foreground sm:inline">
        Step {activeIndex + 1} of {activeSteps.length}
      </span>

      {#if !setupRequired}
        {#if userEmail}
          <div
            class="flex items-center gap-2.5 rounded-full border bg-muted/50 py-1 pl-1 pr-3"
          >
            <span
              class="flex h-6 w-6 items-center justify-center rounded-full bg-primary text-xs font-bold text-primary-foreground"
            >
              {userInitials}
            </span>
            <span class="hidden text-xs text-muted-foreground sm:inline">
              {userEmail}
            </span>
          </div>
        {/if}

        <!-- Marks onboarding complete on the server so it is not offered again. -->
        <Button variant="ghost-muted" size="xs" onclick={handleEnterDashboard}>
          Exit setup
        </Button>
      {/if}
    </div>
  </header>

  <main
    class="relative px-8 py-12 pb-16 max-[900px]:px-5 max-[900px]:py-7 max-[900px]:pb-10"
  >
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
        class="w-full max-w-280 mx-auto grid grid-cols-[320px_1fr] gap-14 items-start max-[900px]:grid-cols-1 max-[900px]:gap-6"
      >
        <aside class="sticky top-8 max-[900px]:static">
          <StepSidebar
            path={setupRequired ? "fresh" : path}
            currentStep={activeIndex}
            steps={activeSteps}
            art={activeStep?.art ?? "welcome"}
            {artProgress}
            onJumpToStep={handleJumpToStep}
          />
        </aside>

        <section
          class="relative rounded-3xl border bg-card text-card-foreground shadow-sm overflow-hidden min-h-135 flex flex-col"
        >
          <div
            class="flex items-center justify-between px-7 py-5 border-b max-[900px]:px-5.5 max-[900px]:py-3.5 max-[900px]:flex-wrap max-[900px]:gap-2.5"
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

          <div class="flex-1 px-5 py-3 max-[900px]:px-4">
            {#if activeStep?.id === "tenant"}
              <TenantIdentity onComplete={handleTenantCreated} />
            {:else if activeStep?.id === "account"}
              <AccountCreation onComplete={handleAccountCreated} />
            {:else if activeStep?.id === "path"}
              <PathChoice bind:path />
            {:else if activeStep?.id === "connect"}
              <NightscoutConnect onComplete={handleMigrationConnected} />
            {:else if activeStep?.id === "cgm"}
              <div class="flex flex-col gap-8 px-4 py-8">
                <div class="flex flex-col items-center gap-4 text-center">
                  <h1
                    class="font-brand font-hairline leading-tight tracking-tight text-3xl md:text-4xl xl:text-5xl"
                  >
                    Connect a <em class="not-italic font-light text-primary">data source</em>.
                  </h1>
                  <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
                    Choose a cloud service or phone app to start sending glucose
                    and treatment data to Nocturne. You can connect more later.
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
                      Enter your credentials and we'll start syncing your data.
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
                      Set up your <em class="not-italic font-light text-primary">app</em>.
                    </h1>
                    <p class="max-w-140 text-base leading-relaxed text-muted-foreground">
                      Follow the steps below to connect your phone app to
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
                onProgressChange={(pct) => (importProgress = pct)}
                onResult={(result) => (importResult = result)}
                onComplete={goToFinish}
              />
            {:else if activeStep?.id === "finish"}
              <Finish
                {path}
                source={sourceResult}
                {importResult}
                onEnterDashboard={handleEnterDashboard}
                onNavigateWithCoach={handleNavigateWithCoach}
              />
            {/if}
          </div>

          {#if !setupRequired}
            <div
              class="flex justify-between items-center px-7 py-4.5 border-t bg-muted/40 max-[900px]:px-5.5 max-[900px]:py-3.5 max-[900px]:flex-wrap max-[900px]:gap-2.5"
            >
              <div>
                {#if stepIndex > 0 && currentStep?.id !== "finish"}
                  <Button variant="outline" onclick={handleBack}>
                    <ArrowLeft class="h-4 w-4" />
                    Back
                  </Button>
                {:else if currentStep?.id === "path"}
                  <span class="text-xs text-muted-foreground">
                    Just pick a starting point.
                  </span>
                {/if}
              </div>
              <div class="flex items-center gap-3">
                {#if currentStep?.id === "finish"}
                  <Button onclick={handleEnterDashboard}>
                    Enter Nocturne
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
  </main>

  <footer
    class="relative px-8 py-5 border-t flex justify-between items-center text-xs text-muted-foreground max-[900px]:flex-wrap max-[900px]:gap-2.5"
  >
    <div class="flex flex-wrap items-center gap-5">
      <span>&copy; 2026 Nocturne</span>
      <a href={resolve("/privacy")} class="hover:text-foreground">Privacy</a>
      <a href="/docs" rel="external" class="hover:text-foreground">Docs</a>
    </div>
    <a
      href="https://github.com/nightscout/nocturne"
      class="inline-flex items-center gap-1.5 hover:text-foreground"
      target="_blank"
      rel="noopener noreferrer"
    >
      <AppLogo class="max-h-12" icon="github" />
      Source
    </a>
  </footer>
</div>
