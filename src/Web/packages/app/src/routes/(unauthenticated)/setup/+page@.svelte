<script lang="ts">
  import { goto } from "$app/navigation";
  import { page } from "$app/state";
  import { ArrowRight, ArrowLeft, Sprout, Cable } from "lucide-svelte";
  import { Button } from "$lib/components/ui/button";
  import Github from "lucide-svelte/icons/github";
  import { markSetupComplete } from "./setup.remote";
  import * as migrationRemote from "$api/generated/migrations.generated.remote";
  import {
    getServicesOverview,
    getActiveDataSources,
    getUploaderSetup,
  } from "$api/generated/services.generated.remote";
  import { MigrationJobState } from "$api";
  import type {
    UploaderApp,
    DataSourceInfo,
  } from "$lib/api/generated/nocturne-api-client";

  import ConstellationCanvas from "./ConstellationCanvas.svelte";
  import StepSidebar from "./StepSidebar.svelte";
  import TenantIdentity from "./steps/TenantIdentity.svelte";
  import AccountCreation from "./steps/AccountCreation.svelte";
  import PathChoice from "./steps/PathChoice.svelte";
  import NightscoutConnect from "./steps/NightscoutConnect.svelte";
  import DataSourceSelectionView from "$lib/components/connectors/DataSourceSelectionView.svelte";
  import ConnectorSetup from "$lib/components/connectors/ConnectorSetup.svelte";
  import UploaderSetupView from "$lib/components/connectors/UploaderSetupView.svelte";
  import ImportProgress from "./steps/ImportProgress.svelte";
  import Finish from "./steps/Finish.svelte";

  // Auth check is handled server-side in +page.server.ts:
  // - setupRequired=true → show two-step setup (tenant identity → account creation)
  // - Subjects exist but not authenticated → redirect to /auth/login
  // - Authenticated → show onboarding wizard

  // ── Setup phase (pre-auth) ──────────────────────────────────────────
  let accountCreated = $state(false);
  const setupRequired = $derived(!accountCreated && page.data?.setupRequired === true);

  const SETUP_STEPS = [
    { id: "tenant", label: "Name your instance", short: "Instance" },
    { id: "account", label: "Create your account", short: "Account" },
  ] as const;

  // If the tenant already exists (user abandoned after step 1), skip to account creation.
  // page.data is resolved before render, so this is correct at init time.
  let setupStepIndex = $state(page.data?.tenantExists === true ? 1 : 0);
  const setupStep = $derived(SETUP_STEPS[setupStepIndex]);
  const setupProgressPct = $derived(
    SETUP_STEPS.length <= 1 ? 100 : (setupStepIndex / (SETUP_STEPS.length - 1)) * 100
  );

  function handleTenantCreated(slug: string) {
    tenantSlug = slug;
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
      { id: "path", label: "Choose your path", short: "Path" },
      { id: "cgm", label: "Connect a data source", short: "Source" },
      { id: "sync", label: "Configure & sync", short: "Setup" },
      { id: "finish", label: "You\u2019re in", short: "Done" },
    ],
    migration: [
      { id: "path", label: "Choose your path", short: "Path" },
      { id: "connect", label: "Connect your Nightscout", short: "Connect" },
      { id: "import", label: "Import your history", short: "Import" },
      { id: "finish", label: "Welcome home", short: "Done" },
    ],
  } as const;

  // ── State ───────────────────────────────────────────────────────────
  let path = $state<"fresh" | "migration">("fresh");
  let stepIndex = $state(0);
  let tenantSlug = $state<string | null>(null);
  let selectedConnectorId = $state<string | null>(null);
  let selectedUploader = $state<UploaderApp | null>(null);
  // ── Reactive service queries (auto-activate when setup completes) ──
  // query() returns reactive objects anchored to this component's lifecycle.
  // They cannot be awaited in event handlers — must live in $derived context.
  const servicesQuery = $derived(!setupRequired ? getServicesOverview() : null);
  const dataSourcesQuery = $derived(!setupRequired ? getActiveDataSources() : null);
  const uploaderSetupQuery = $derived(
    selectedUploader?.id ? getUploaderSetup(selectedUploader.id) : null
  );

  const servicesData = $derived(servicesQuery?.current ?? null);
  const activeDataSources = $derived<DataSourceInfo[]>(dataSourcesQuery?.current ?? []);
  const uploaderSetupResponse = $derived(uploaderSetupQuery?.current ?? null);
  const servicesLoading = $derived(
    servicesQuery == null ||
    servicesQuery.current === undefined ||
    dataSourcesQuery == null ||
    dataSourcesQuery.current === undefined
  );
  let importProgress = $state(0);
  let migrationJobId = $state<string | undefined>(undefined);

  const steps = $derived(STEPS[path]);
  const currentStep = $derived(steps[stepIndex]);
  const progressPct = $derived(
    steps.length <= 1 ? 100 : (stepIndex / (steps.length - 1)) * 100
  );

  // Constellation progress: for migration import step, use import %; otherwise step-based
  const constellationProgress = $derived.by(() => {
    if (path === "migration" && currentStep?.id === "import") {
      return 0.35 + 0.6 * (importProgress / 100);
    }
    if (currentStep?.id === "finish") return 1;
    return steps.length <= 1 ? 1 : stepIndex / (steps.length - 1);
  });

  // ── Data source loading ──────────────────────────────────────────────
  const connectors = $derived(servicesData?.availableConnectors ?? []);
  const uploaderApps = $derived(servicesData?.uploaderApps ?? []);

  // ── Onboarding CSS variables ──────────────────────────────────────────
  // Path-independent surface/utility tokens
  const BASE_VARS = [
    "--onb-navy: oklch(0.08 0.025 261.692)",
    "--onb-navy-60: oklch(0.08 0.025 261.692 / 0.6)",
    "--onb-navy-50: oklch(0.08 0.025 261.692 / 0.5)",
    "--onb-panel: oklch(0.09 0.022 261.692)",
    "--onb-surface: oklch(0.1 0.025 261.692)",
    "--onb-surface-60: oklch(0.1 0.025 261.692 / 0.6)",
    "--onb-teal: oklch(0.72 0.14 184)",
    "--onb-ok: oklch(0.72 0.17 150)",
    "--onb-green: oklch(0.78 0.21 145)",
    "--onb-green-soft: oklch(0.85 0.21 145)",
    "--onb-green-dim: oklch(0.78 0.21 145 / 0.12)",
    "--onb-lavender: oklch(0.78 0.09 265)",
    "--onb-lavender-soft: oklch(0.86 0.08 265)",
    "--onb-lavender-dim: oklch(0.78 0.09 265 / 0.14)",
    "--onb-border: rgb(255 255 255 / 0.08)",
  ].join("; ");

  // Path-dependent accent tokens
  const accentVars = $derived(
    path === "migration"
      ? "--onb-accent: var(--onb-lavender); --onb-accent-soft: var(--onb-lavender-soft); --onb-accent-dim: var(--onb-lavender-dim)"
      : "--onb-accent: var(--onb-green); --onb-accent-soft: var(--onb-green-soft); --onb-accent-dim: var(--onb-green-dim)"
  );

  const styleVars = $derived(`${BASE_VARS}; ${accentVars}`);

  // ── Navigation ──────────────────────────────────────────────────────
  function handlePathSelect(selected: "fresh" | "migration") {
    path = selected;
    stepIndex = 1;
  }

  function handleBack() {
    if (stepIndex > 0) stepIndex--;
  }

  function handleNext() {
    if (stepIndex < steps.length - 1) stepIndex++;
  }

  function handleSkip() {
    handleNext();
  }

  async function handleEnterDashboard() {
    await markSetupComplete();
    await goto("/", { invalidateAll: true });
  }

  async function handleNavigateWithCoach(url: string) {
    await markSetupComplete();
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

  function handleSourceSkip() {
    const finishIdx = steps.findIndex((s) => s.id === "finish");
    if (finishIdx >= 0) stepIndex = finishIdx;
  }

  function handleSetupComplete() {
    const finishIdx = steps.findIndex((s) => s.id === "finish");
    if (finishIdx >= 0) stepIndex = finishIdx;
  }

  function handleSetupBack() {
    handleBack();
  }

  function handleImportComplete() {
    const finishIdx = steps.findIndex((s) => s.id === "finish");
    if (finishIdx >= 0) stepIndex = finishIdx;
  }

  async function handleMigrationConnected() {
    // Check for an active migration job started by the connector
    try {
      const history = await migrationRemote.getHistory();
      const activeJob = history?.find(
        (j) =>
          j.state === MigrationJobState.Running ||
          j.state === MigrationJobState.Pending ||
          j.state === MigrationJobState.Validating
      );
      if (activeJob?.id) {
        migrationJobId = activeJob.id;
      }
    } catch {
      // ImportProgress will find the job itself if we can't here
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
  class="relative min-h-screen grid grid-rows-[auto_1fr_auto] text-white"
  style="{styleVars}; background: var(--onb-navy);"
>
  <!-- Background gradient -->
  <div
    class="fixed inset-0 z-0 pointer-events-none"
    style="background: radial-gradient(ellipse 50% 35% at 50% 0%, oklch(0.16 0.05 265 / 0.6), transparent 70%), linear-gradient(180deg, var(--onb-navy), oklch(0.07 0.03 261.692));"
  ></div>

  <!-- Constellation background (above gradient so stars are visible) -->
  <div class="fixed inset-0 z-1 pointer-events-none">
    <ConstellationCanvas progress={constellationProgress} />
  </div>

  <!-- Header -->
  <header
    class="relative z-50 flex items-center justify-between px-8 py-5.5 border-b border-white/8 backdrop-blur-sm max-[900px]:px-5 max-[900px]:py-3.5"
    style="background: var(--onb-navy-60); backdrop-filter: blur(14px) saturate(1.3);"
  >
    <div class="flex items-center gap-3">
      <!-- Logo mark -->
      <svg class="h-7 w-7" viewBox="0 0 100 100" aria-hidden="true">
        <path
          d="M50 6 C 70 30, 86 48, 86 66 A 36 36 0 0 1 14 66 C 14 48, 30 30, 50 6 Z"
          fill="oklch(0.21 0.04 265)"
        />
        <path
          d="M58 32 A 28 28 0 1 0 58 92 A 22 22 0 1 1 58 32 Z"
          fill="oklch(0.82 0.06 265)"
          opacity="0.92"
        />
        <g fill="#22c55e">
          <circle cx="18" cy="56" r="1.6" />
          <circle cx="26" cy="58" r="1.6" />
          <circle cx="34" cy="58" r="1.6" />
          <circle cx="42" cy="56" r="1.6" />
          <circle cx="50" cy="55" r="1.6" />
          <circle cx="58" cy="55" r="1.6" />
          <circle cx="66" cy="56" r="1.6" />
          <circle cx="74" cy="58" r="1.6" />
          <circle cx="82" cy="60" r="1.6" />
        </g>
      </svg>
      <span
        class="font-[Montserrat] text-xl font-light tracking-wide text-white"
      >
        nocturne
      </span>
    </div>

    <div class="flex items-center gap-5">
      {#if setupRequired}
        <!-- Step counter for setup phase -->
        <span class="hidden font-mono text-[13px] text-white/40 sm:inline">
          Step {setupStepIndex + 1} of {SETUP_STEPS.length}
        </span>
      {:else}
        <!-- Step counter -->
        <span class="hidden font-mono text-[13px] text-white/40 sm:inline">
          Step {stepIndex + 1} of {steps.length}
        </span>

        <!-- Account pill -->
        {#if userEmail}
          <div
            class="flex items-center gap-2.5 rounded-full border border-white/8 bg-white/3 py-1 pl-1 pr-3"
          >
            <span
              class="flex h-6 w-6 items-center justify-center rounded-full text-[11px] font-bold"
              style="background: linear-gradient(135deg, var(--onb-teal), var(--onb-accent)); color: var(--onb-navy);"
            >
              {userInitials}
            </span>
            <span class="hidden text-[13px] text-white/40 sm:inline">
              {userEmail}
            </span>
          </div>
        {/if}

        <!-- Save & exit -->
        <button
          class="text-[13px] text-white/40 transition-colors hover:text-white"
          onclick={handleEnterDashboard}
        >
          Save & exit
        </button>
      {/if}
    </div>
  </header>

  <!-- Stage -->
  <main
    class="relative z-10 px-8 py-12 pb-16 max-[900px]:px-5 max-[900px]:py-7 max-[900px]:pb-10"
  >
    {#if setupRequired}
      <!-- ═══ Pre-auth setup: tenant identity → account creation ═══ -->
      <div
        class="w-full max-w-280 mx-auto grid grid-cols-[320px_1fr] gap-14 items-start max-[900px]:grid-cols-1 max-[900px]:gap-6"
      >
        <!-- Sidebar -->
        <aside class="sticky top-25 max-[900px]:static">
          <StepSidebar
            path="fresh"
            currentStep={setupStepIndex}
            steps={SETUP_STEPS.map((s) => ({ id: s.id, label: s.label }))}
            onJumpToStep={(i) => {
              if (i <= setupStepIndex) setupStepIndex = i;
            }}
          />
        </aside>

        <!-- Step card -->
        <section
          class="step-card relative rounded-[22px] border border-white/8 backdrop-blur-[18px] overflow-hidden min-h-135 flex flex-col"
          style="background: linear-gradient(180deg, oklch(0.14 0.03 261.692 / 0.85), oklch(0.12 0.025 261.692 / 0.75)); box-shadow: 0 1px 0 rgb(255 255 255 / 0.05) inset, 0 30px 80px -30px rgb(0 0 0 / 0.6);"
        >
          <!-- Strip -->
          <div
            class="relative z-2 flex items-center justify-between px-7 py-5 border-b border-white/8 max-[900px]:px-5.5 max-[900px]:py-3.5 max-[900px]:flex-wrap max-[900px]:gap-2.5"
          >
            <div class="flex items-center gap-3">
              <span
                class="font-mono text-xs uppercase tracking-wide text-white/40"
              >
                Step {String(setupStepIndex + 1).padStart(2, "0")} / {String(
                  SETUP_STEPS.length
                ).padStart(2, "0")}
              </span>
              <span class="text-white/20">&middot;</span>
              <span class="text-xs text-white/40">{setupStep?.short}</span>
            </div>
            <div class="flex items-center gap-3">
              <!-- Progress bar -->
              <div class="h-0.75 w-30 overflow-hidden rounded-full bg-white/8">
                <div
                  class="h-full rounded-full transition-all duration-500"
                  style="width: {setupProgressPct}%; background: var(--onb-teal); box-shadow: 0 0 10px var(--onb-teal);"
                ></div>
              </div>
            </div>
          </div>

          <!-- Step body -->
          <div class="relative z-2 flex-1 px-5 py-3 max-[900px]:px-4">
            {#if setupStep?.id === "tenant"}
              <TenantIdentity onComplete={handleTenantCreated} />
            {:else if setupStep?.id === "account"}
              <AccountCreation onComplete={handleAccountCreated} />
            {/if}
          </div>
        </section>
      </div>
    {:else}
      <!-- ═══ Post-auth onboarding wizard ═══ -->
      <div
        class="w-full max-w-280 mx-auto grid grid-cols-[320px_1fr] gap-14 items-start max-[900px]:grid-cols-1 max-[900px]:gap-6"
      >
        <!-- Sidebar -->
        <aside class="sticky top-25 max-[900px]:static">
          <StepSidebar
            {path}
            currentStep={stepIndex}
            steps={steps.map((s) => ({ id: s.id, label: s.label }))}
            onJumpToStep={(i) => (stepIndex = i)}
          />
        </aside>

        <!-- Step card -->
        <section
          class="step-card relative rounded-[22px] border border-white/8 backdrop-blur-[18px] overflow-hidden min-h-135 flex flex-col"
          style="background: linear-gradient(180deg, oklch(0.14 0.03 261.692 / 0.85), oklch(0.12 0.025 261.692 / 0.75)); box-shadow: 0 1px 0 rgb(255 255 255 / 0.05) inset, 0 30px 80px -30px rgb(0 0 0 / 0.6);"
        >
          <!-- Strip -->
          <div
            class="relative z-2 flex items-center justify-between px-7 py-5 border-b border-white/8 max-[900px]:px-5.5 max-[900px]:py-3.5 max-[900px]:flex-wrap max-[900px]:gap-2.5"
          >
            <div class="flex items-center gap-3">
              <span
                class="font-mono text-xs uppercase tracking-wide text-white/40"
              >
                Step {String(stepIndex + 1).padStart(2, "0")} / {String(
                  steps.length
                ).padStart(2, "0")}
              </span>
              <span class="text-white/20">&middot;</span>
              <span class="text-xs text-white/40">{currentStep?.short}</span>
            </div>
            <div class="flex items-center gap-3">
              <!-- Path badge -->
              <span
                class="inline-flex items-center gap-1.5 px-2.5 py-1 rounded-full text-[10.5px] font-semibold tracking-[0.06em] uppercase"
                style="background: var(--onb-accent-dim); color: var(--onb-accent); border: 1px solid var(--onb-accent-dim);"
              >
                {#if path === "migration"}
                  <Cable class="h-2.5 w-2.5" />
                  Nightscout Migration
                {:else}
                  <Sprout class="h-2.5 w-2.5" />
                  Fresh Start
                {/if}
              </span>
              <!-- Progress bar -->
              <div class="h-0.75 w-30 overflow-hidden rounded-full bg-white/8">
                <div
                  class="h-full rounded-full transition-all duration-500"
                  style="width: {progressPct}%; background: var(--onb-accent); box-shadow: 0 0 10px var(--onb-accent);"
                ></div>
              </div>
            </div>
          </div>

          <!-- Step body -->
          <div class="relative z-2 flex-1 px-5 py-3 max-[900px]:px-4">
            {#if currentStep?.id === "path"}
              <PathChoice currentPath={path} onSelect={handlePathSelect} />
            {:else if currentStep?.id === "connect"}
              <NightscoutConnect onComplete={handleMigrationConnected} />
            {:else if currentStep?.id === "cgm"}
              <div class="flex flex-col gap-8 px-4 py-8">
                <div class="flex flex-col items-center gap-4 text-center">
                  <h1
                    class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
                    style="font-size: clamp(32px, 4vw, 48px);"
                  >
                    Connect a <em
                      class="not-italic font-light"
                      style="color: var(--onb-accent);"
                    >
                      data source
                    </em>
                    .
                  </h1>
                  <p class="max-w-140 text-base leading-relaxed text-white/50">
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
                  onSkip={handleSourceSkip}
                />
              </div>
            {:else if currentStep?.id === "sync"}
              <div class="flex flex-col gap-8 px-4 py-8">
                {#if selectedConnectorId}
                  <div class="flex flex-col items-center gap-4 text-center">
                    <h1
                      class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
                      style="font-size: clamp(32px, 4vw, 48px);"
                    >
                      Configure your <em
                        class="not-italic font-light"
                        style="color: var(--onb-accent);"
                      >
                        connection
                      </em>
                      .
                    </h1>
                    <p class="max-w-140 text-base leading-relaxed text-white/50">
                      Enter your credentials and we'll start syncing your data.
                    </p>
                  </div>
                  <ConnectorSetup
                    connectorId={selectedConnectorId}
                    primaryAction="save-and-sync"
                    showToggle={false}
                    showDangerZone={false}
                    showCapabilities={true}
                    onComplete={() => handleSetupComplete()}
                    onCancel={handleSetupBack}
                  />
                {:else if selectedUploader}
                  <div class="flex flex-col items-center gap-4 text-center">
                    <h1
                      class="font-[Montserrat] font-[250] leading-tight tracking-tight text-white"
                      style="font-size: clamp(32px, 4vw, 48px);"
                    >
                      Set up your <em
                        class="not-italic font-light"
                        style="color: var(--onb-accent);"
                      >
                        app
                      </em>
                      .
                    </h1>
                    <p class="max-w-140 text-base leading-relaxed text-white/50">
                      Follow the steps below to connect your phone app to
                      Nocturne.
                    </p>
                  </div>
                  <UploaderSetupView
                    app={selectedUploader}
                    setupResponse={uploaderSetupResponse}
                    onBack={handleSetupBack}
                    onConnected={handleSetupComplete}
                  />
                {:else}
                  <div
                    class="flex flex-col items-center gap-8 px-4 py-8 text-center"
                  >
                    <p class="text-white/50">
                      No data source selected. Go back to choose one.
                    </p>
                  </div>
                {/if}
              </div>
            {:else if currentStep?.id === "import"}
              <ImportProgress
                jobId={migrationJobId}
                onProgressChange={(pct) => (importProgress = pct)}
                onComplete={handleImportComplete}
              />
            {:else if currentStep?.id === "finish"}
              <Finish {path} onEnterDashboard={handleEnterDashboard} onNavigateWithCoach={handleNavigateWithCoach} />
            {/if}
          </div>

          <!-- Actions bar -->
          <div
            class="relative z-2 flex justify-between items-center px-7 py-4.5 border-t border-white/8 max-[900px]:px-5.5 max-[900px]:py-3.5 max-[900px]:flex-wrap max-[900px]:gap-2.5"
            style="background: var(--onb-surface-60);"
          >
            <div>
              {#if stepIndex > 0 && currentStep?.id !== "finish"}
                <Button variant="outline" onclick={handleBack}>
                  <ArrowLeft class="h-4 w-4" />
                  Back
                </Button>
              {:else if currentStep?.id === "path"}
                <span class="text-xs text-white/30">
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
              {:else if currentStep?.id === "cgm"}
                <Button onclick={handleSetupComplete}>
                  Save and continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {:else if currentStep?.id !== "path" && currentStep?.id !== "sync"}
                <Button variant="ghost" onclick={handleSkip}>
                  Skip for now
                </Button>
                <Button onclick={handleNext}>
                  Continue
                  <ArrowRight class="h-4 w-4" />
                </Button>
              {/if}
            </div>
          </div>
        </section>
      </div>
    {/if}
  </main>

  <!-- Footer -->
  <footer
    class="relative z-50 px-8 py-5 border-t border-white/8 flex justify-between items-center text-xs text-white/30 max-[900px]:flex-wrap max-[900px]:gap-2.5"
    style="background: var(--onb-navy-50); backdrop-filter: blur(8px);"
  >
    <div class="flex flex-wrap items-center gap-5">
      <span>&copy; 2026 Nocturne</span>
      <a href="/privacy" class="hover:text-white/60">Privacy</a>
      <a href="/docs" class="hover:text-white/60">Docs</a>
    </div>
    <div class="flex flex-wrap items-center gap-5">
      <span class="font-mono">v1.4.2</span>
      <a
        href="https://github.com/nightscout/nocturne"
        class="inline-flex items-center gap-1.5 hover:text-white/60"
        target="_blank"
        rel="noopener noreferrer"
      >
        <Github class="h-3.5 w-3.5" />
        Source
      </a>
    </div>
  </footer>
</div>

<style>
  /* Pseudo-element for step-card accent glow — cannot be expressed with Tailwind's before: variant
     because it uses a CSS custom property in the radial-gradient. */
  .step-card::before {
    content: "";
    position: absolute;
    inset: 0;
    pointer-events: none;
    background: radial-gradient(
      ellipse 60% 30% at 50% 0%,
      var(--onb-accent-dim),
      transparent 70%
    );
  }
</style>
