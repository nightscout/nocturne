<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "#lib/components/ui/card/index.js";
  import { Button } from "#lib/components/ui/button/index.js";
  import { Badge } from "#lib/components/ui/badge/index.js";
  import { Separator } from "#lib/components/ui/separator/index.js";
  import { Switch } from "#lib/components/ui/switch/index.js";
  import { Label } from "#lib/components/ui/label/index.js";
  import { Textarea } from "#lib/components/ui/textarea/index.js";
  import GithubIcon from "#lib/components/icons/GithubIcon.svelte";
  import LifeBuoy from "@lucide/svelte/icons/life-buoy";
  import FileText from "@lucide/svelte/icons/file-text";
  import Bug from "@lucide/svelte/icons/bug";
  import ExternalLink from "@lucide/svelte/icons/external-link";
  import Copy from "@lucide/svelte/icons/copy";
  import Download from "@lucide/svelte/icons/download";
  import Shield from "@lucide/svelte/icons/shield";
  import Heart from "@lucide/svelte/icons/heart";
  import Users from "@lucide/svelte/icons/users";
  import HelpCircle from "@lucide/svelte/icons/circle-question-mark";
  import CheckCircle from "@lucide/svelte/icons/circle-check-big";
  import Lightbulb from "@lucide/svelte/icons/lightbulb";
  import Database from "@lucide/svelte/icons/database";
  import CreditCard from "@lucide/svelte/icons/credit-card";
  import GraduationCap from "@lucide/svelte/icons/graduation-cap";
  import { DropGroup, DropSurface } from "water-ui";
  import { getServicesOverview } from "#lib/api/generated/services.generated.remote.js";
  import { getStatus } from "#lib/api/generated/status.generated.remote.js";
  import { getSupportConfig } from "#lib/api/support.remote.js";
  import { formatDateTime } from "#lib/utils/formatting.js";
  import IssueCreatorDialog from "#lib/components/support/IssueCreatorDialog.svelte";
  import { getCoachMarkContext } from "@nocturne/coach";
  import { toast } from "svelte-sonner";
  import { createCopyFeedback } from "#lib/hooks/copy-feedback.svelte.js";
  import { describeSubmitError } from "#lib/forms/submit-error.js";
  import {
    buildDiagnosticReport,
    readDiagnosticDevice,
  } from "./diagnostic-report";

  let includeDeviceInfo = $state(true);
  let additionalDetails = $state("");
  const copy = createCopyFeedback();

  let dialogOpen = $state(false);
  let selectedTemplate = $state("bug");

  // Read results via .current rather than an `{#await}` block: consuming a remote
  // query through its thenable reads the hydration cache during hydration and throws
  // hydratable_missing_but_required. .current defers the fetch past hydration. The
  // inner `{#if}` guards already handle the undefined-until-loaded value.
  const servicesOverviewQuery = getServicesOverview();
  const supportConfigQuery = getSupportConfig();
  const statusQuery = getStatus();
  const supportConfig = $derived(supportConfigQuery.current);
  const services = $derived(servicesOverviewQuery.current);
  const status = $derived(statusQuery.current);

  let useOperatorSupport = $state(false);

  const coachCtx = getCoachMarkContext();
  let resettingTutorials = $state(false);
  const tutorialsDisabled = $derived(coachCtx.disabled);

  async function resetTutorials() {
    resettingTutorials = true;
    try {
      await coachCtx.resetAll();
      toast.success("Tutorials reset. They will appear as you use the app");
    } catch (err) {
      toast.error(describeSubmitError(err, "Failed to reset tutorials"));
    } finally {
      resettingTutorials = false;
    }
  }

  function setTutorialsDisabled(value: boolean) {
    coachCtx.setDisabled(value);
    toast.success(value ? "Tutorials disabled" : "Tutorials enabled");
  }

  const supportOptions = [
    {
      name: "Report a bug",
      description: "Found something not working? Let us know",
      icon: Bug,
      template: "bug",
    },
    {
      name: "Request a feature",
      description: "Have an idea? Tell us about it",
      icon: Lightbulb,
      template: "feature",
    },
    {
      name: "Data issue",
      description: "CGM data problems or missing readings",
      icon: Database,
      template: "data-issue",
    },
    {
      name: "Account / billing",
      description: "Help with your account or subscription",
      icon: CreditCard,
      template: "account",
    },
  ];

  async function copyLogs() {
    await copy.copy(generateDiagnosticReport());
  }

  function downloadLogs() {
    const logs = generateDiagnosticReport();
    const blob = new Blob([logs], { type: "text/plain" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `nocturne-logs-${new Date().toISOString().split("T")[0]}.txt`;
    a.click();
    URL.revokeObjectURL(url);
  }

  function generateDiagnosticReport(): string {
    return buildDiagnosticReport({
      timestamp: new Date().toISOString(),
      build: status
        ? { version: status.version, head: status.head, build: status.build }
        : null,
      includeDeviceInfo,
      device: readDiagnosticDevice(),
      additionalDetails,
    });
  }

  function handleSupportAction(template: string, accountBillingMode?: string | null) {
    selectedTemplate = template;
    useOperatorSupport = template === "account" && accountBillingMode === "api";
    dialogOpen = true;
  }
</script>

<svelte:head>
  <title>Support - Settings - Nocturne</title>
</svelte:head>

<div class="@container container mx-auto max-w-4xl p-3 @md:p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 shrink-0 items-center justify-center rounded-xl bg-primary/10">
      <LifeBuoy class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Support</h1>
      <p class="text-muted-foreground">
        Get help, report problems, and share feedback
      </p>
    </div>
  </div>

  <!-- Support Options -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <HelpCircle class="h-5 w-5" />
        Get support
      </CardTitle>
      <CardDescription>Need help? Here's how to reach us</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <!-- No `fonts`: these sizes are inherited, so there is no class to mirror. -->
      <DropGroup name="support options">
      <div class="grid gap-4 @xl:grid-cols-2">
        {#each supportOptions as option (option.name)}
          {#if option.template === "account" && supportConfig?.accountBilling?.mode === "redirect"}
            <DropSurface
              as="a"
              name={option.template}
              palette="water"
              peak={0.6}
              href={supportConfig.accountBilling.url}
              target="_blank"
              rel="external noopener noreferrer"
              class="rounded-lg border border-border transition-colors hover:not-disabled:border-primary/50 hover:not-disabled:bg-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
              contentClass="flex flex-col items-center gap-4 p-4"
            >
              <div
                data-drop-obstacle
                class="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
              >
                <ExternalLink class="h-6 w-6 text-primary" />
              </div>
              <div class="text-center">
                <span class="font-medium">{supportConfig.accountBilling.label ?? option.name}</span>
                <p class="text-sm text-muted-foreground mt-1">
                  {option.description}
                </p>
              </div>
            </DropSurface>
          {:else}
            <!-- The account tile's routing depends on the operator config; keep it inert until
                 the config resolves so a click during the fetch window can't misroute a
                 redirect/api-mode tenant to the generic community dialog. Other templates route
                 the same regardless of config, so they stay interactive. -->
            <DropSurface
              as="button"
              type="button"
              name={option.template}
              palette="water"
              peak={0.6}
              class="rounded-lg border border-border transition-colors hover:not-disabled:border-primary/50 hover:not-disabled:bg-accent/50 disabled:cursor-not-allowed disabled:opacity-60"
              contentClass="flex flex-col items-center gap-4 p-4"
              disabled={option.template === "account" && supportConfig === undefined}
              onclick={() => handleSupportAction(option.template, supportConfig?.accountBilling?.mode)}
            >
              <div
                data-drop-obstacle
                class="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
              >
                <option.icon class="h-6 w-6 text-primary" />
              </div>
              <div class="text-center">
                <span class="font-medium">{option.name}</span>
                <p class="text-sm text-muted-foreground mt-1">
                  {option.description}
                </p>
              </div>
            </DropSurface>
          {/if}
        {/each}
      </div>
      </DropGroup>

      <div class="flex justify-center pt-2">
        <a
          href="https://discord.gg/xWYz9fFWrj"
          target="_blank"
          rel="noopener noreferrer"
        >
          <Button variant="outline">
            <Users class="h-4 w-4" />
            Get help on Discord
            <ExternalLink class="h-3 w-3" />
          </Button>
        </a>
      </div>
    </CardContent>
  </Card>

  <!-- Tutorials -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <GraduationCap class="h-5 w-5" />
        Tutorials
      </CardTitle>
      <CardDescription>Guided walkthroughs to help you learn the app</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <div class="space-y-0.5">
          <p class="text-sm font-medium">Show all tutorials again</p>
          <p class="text-sm text-muted-foreground">
            Reset all guided walkthroughs so they appear as you navigate
          </p>
        </div>
        <Button
          variant="outline"
          onclick={resetTutorials}
          disabled={resettingTutorials}
        >
          <GraduationCap class="h-4 w-4" />
          {resettingTutorials ? "Resetting..." : "Reset tutorials"}
        </Button>
      </div>

      <Separator />

      <div class="flex items-center justify-between">
        <div class="space-y-0.5">
          <Label for="disable-tutorials">Disable all tutorials</Label>
          <p class="text-sm text-muted-foreground">
            Stop showing guided walkthroughs anywhere in the app
          </p>
        </div>
        <Switch
          id="disable-tutorials"
          checked={tutorialsDisabled}
          onCheckedChange={setTutorialsDisabled}
        />
      </div>
    </CardContent>
  </Card>

  <!-- Share Logs -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <FileText class="h-5 w-5" />
        Share diagnostic logs
      </CardTitle>
      <CardDescription>Export logs to help troubleshoot issues</CardDescription>
    </CardHeader>
    <CardContent class="space-y-6">
      <div class="space-y-4">
        <div class="flex flex-wrap items-center justify-between gap-3">
          <div class="space-y-0.5">
            <Label>Include device information</Label>
            <p class="text-sm text-muted-foreground">
              Browser, OS, and screen size
            </p>
          </div>
          <Switch bind:checked={includeDeviceInfo} />
        </div>
      </div>

      <Separator />

      <div class="space-y-2">
        <Label>Additional details (optional)</Label>
        <Textarea
          bind:value={additionalDetails}
          placeholder="Describe what you were doing when the issue occurred..."
          rows={3}
        />
      </div>

      <div class="flex flex-wrap gap-2">
        <Button variant="outline" onclick={copyLogs}>
          {#if copy.isCopied()}
            <CheckCircle class="h-4 w-4 text-success" />
            Copied
          {:else}
            <Copy class="h-4 w-4" />
            Copy to clipboard
          {/if}
        </Button>
        <Button variant="outline" onclick={downloadLogs}>
          <Download class="h-4 w-4" />
          Download logs
        </Button>
      </div>

      <Card variant="info">
        <CardContent class="flex items-start gap-3 pt-6">
          <Shield
            class="h-5 w-5 text-info shrink-0 mt-0.5"
          />
          <div>
            <p class="font-medium text-info">
              Privacy note
            </p>
            <p class="text-sm text-info">
              Logs never include your glucose data, API tokens, or passwords.
              Only diagnostic information is shared.
            </p>
          </div>
        </CardContent>
      </Card>
    </CardContent>
  </Card>

  <!-- About Section -->
  <Card>
    <CardHeader>
      <CardTitle>About Nocturne</CardTitle>
    </CardHeader>
    <CardContent class="space-y-4">
      {#if services?.apiEndpoint?.baseUrl}
        <div class="flex flex-wrap items-center justify-between gap-x-4 py-2 border-b">
          <span class="text-muted-foreground">API endpoint</span>
          <span class="min-w-0 break-all font-mono text-sm">{services.apiEndpoint.baseUrl}</span>
        </div>
      {/if}
      {#if status?.head && status.head !== "unknown" && status.head !== "nocturne-dev"}
        <div class="flex items-center justify-between py-2 border-b">
          <span class="text-muted-foreground">Commit</span>
          <a
            href={`https://github.com/nightscout/nocturne/commit/${status.head}`}
            target="_blank"
            rel="noopener noreferrer"
            class="font-mono text-sm inline-flex items-center gap-1 hover:text-primary hover:underline"
          >
            {status.head.slice(0, 7)}
            <ExternalLink class="h-3 w-3" />
          </a>
        </div>
      {/if}
      {#if status?.build}
        <div class="flex items-center justify-between py-2 border-b">
          <span class="text-muted-foreground">Built</span>
          <span class="font-mono text-sm">{formatDateTime(status.build)}</span>
        </div>
      {/if}
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">License</span>
        <span>AGPL-3.0</span>
      </div>
      <div class="flex items-center justify-between py-2">
        <span class="text-muted-foreground">API compatibility</span>
        <Badge variant="secondary">Nightscout v1-v4</Badge>
      </div>

      <Separator class="my-4" />

      <div class="text-center text-sm text-muted-foreground">
        <p>
          Made with <Heart class="h-4 w-4 inline text-destructive" /> by the Nightscout
          community
        </p>
        <p class="mt-2">
          Nocturne is free and open source software, created by people with
          diabetes, for people with diabetes.
        </p>
      </div>

      <div class="flex justify-center gap-4 pt-4">
        <a
          href="https://github.com/nightscout/nocturne"
          target="_blank"
          rel="noopener noreferrer"
        >
          <Button variant="ghost" size="sm">
            <GithubIcon class="h-4 w-4" />
            Star on GitHub
          </Button>
        </a>
        <a
          href="https://www.nightscoutfoundation.org/donate"
          target="_blank"
          rel="noopener noreferrer"
        >
          <Button variant="ghost" size="sm">
            <Heart class="h-4 w-4" />
            Donate
          </Button>
        </a>
      </div>
    </CardContent>
  </Card>
</div>

<IssueCreatorDialog bind:open={dialogOpen} template={selectedTemplate} {useOperatorSupport} />
