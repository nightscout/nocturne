<script lang="ts">
  import {
    Card,
    CardContent,
    CardDescription,
    CardHeader,
    CardTitle,
  } from "$lib/components/ui/card";
  import { Button } from "$lib/components/ui/button";
  import { Badge } from "$lib/components/ui/badge";
  import { Separator } from "$lib/components/ui/separator";
  import { Switch } from "$lib/components/ui/switch";
  import { Label } from "$lib/components/ui/label";
  import { Textarea } from "$lib/components/ui/textarea";
  import {
    HeartHandshake,
    Github,
    MessageCircle,
    FileText,
    Bug,
    ExternalLink,
    Copy,
    Download,
    Shield,
    Heart,
    Users,
    BookOpen,
    HelpCircle,
    CheckCircle,
    Lightbulb,
    Database,
    CreditCard,
    GraduationCap,
  } from "lucide-svelte";
  import { getServicesOverview } from "$api";
  import type { ServicesOverview, SupportConfigResponse } from "$api";
  import { getSupportConfig } from "$lib/api/support.remote";
  import IssueCreatorDialog from "$lib/components/support/IssueCreatorDialog.svelte";
  import { getCoachMarkContext } from "@nocturne/coach";
  import { toast } from "svelte-sonner";

  let includeDeviceInfo = $state(true);
  let includeRecentLogs = $state(true);
  let includeSettings = $state(false);
  let additionalDetails = $state("");
  let logsCopied = $state(false);

  let dialogOpen = $state(false);
  let selectedTemplate = $state("bug");

  let apiBaseUrl = $state<string | null>(null);

  const servicesOverviewQuery = $derived(getServicesOverview());
  const supportConfigQuery = $derived(getSupportConfig());

  const services = $derived(servicesOverviewQuery.current as ServicesOverview | undefined);
  const supportConfig = $derived(supportConfigQuery.current as SupportConfigResponse | undefined);

  let useOperatorSupport = $state(false);

  const coachCtx = getCoachMarkContext();
  let resettingTutorials = $state(false);

  async function resetTutorials() {
    resettingTutorials = true;
    try {
      await coachCtx.resetAll();
      toast.success("Tutorials reset — they'll appear as you navigate the app");
    } catch {
      toast.error("Failed to reset tutorials");
    } finally {
      resettingTutorials = false;
    }
  }

  $effect(() => {
    if (services?.apiEndpoint) {
      apiBaseUrl = services.apiEndpoint.baseUrl || null;
    }
  });

  const communityLinks = $derived([
    {
      name: "GitHub Repository",
      description: "Source code, issues, and feature requests",
      icon: Github,
      href: "https://github.com/nightscout/nocturne",
      badge: "Open Source",
    },
    {
      name: "Discord Community",
      description: "Chat with developers and other users",
      icon: MessageCircle,
      href: "https://discord.gg/xWYz9fFWrj",
      badge: "Active",
    },
    {
      name: "Documentation",
      description: "Guides, tutorials, and API reference",
      icon: BookOpen,
      href: "https://docs.nightscout.info/",
    },
    {
      name: "Nightscout Foundation",
      description: "The organization behind Nightscout",
      icon: Heart,
      href: "https://www.nightscoutfoundation.org/",
      badge: "501(c)(3)",
    },
  ]);

  const supportOptions = [
    {
      name: "Report a Bug",
      description: "Found something not working? Let us know",
      icon: Bug,
      template: "bug",
    },
    {
      name: "Request a Feature",
      description: "Have an idea? We'd love to hear it",
      icon: Lightbulb,
      template: "feature",
    },
    {
      name: "Data Issue",
      description: "CGM data problems or missing readings",
      icon: Database,
      template: "data-issue",
    },
    {
      name: "Account / Billing",
      description: "Help with your account or subscription",
      icon: CreditCard,
      template: "account",
    },
  ];

  async function copyLogs() {
    const logs = generateDiagnosticReport();
    await navigator.clipboard.writeText(logs);
    logsCopied = true;
    setTimeout(() => (logsCopied = false), 2000);
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
    const report = {
      timestamp: new Date().toISOString(),
      version: "1.0.0",
      userAgent:
        typeof navigator !== "undefined" ? navigator.userAgent : "unknown",
      platform:
        typeof navigator !== "undefined" ? navigator.platform : "unknown",
      screenSize:
        typeof window !== "undefined"
          ? `${window.innerWidth}x${window.innerHeight}`
          : "unknown",
      deviceInfo: includeDeviceInfo,
      recentLogs: includeRecentLogs,
      settingsIncluded: includeSettings,
      additionalDetails: additionalDetails,
    };
    return JSON.stringify(report, null, 2);
  }

  function handleSupportAction(template: string) {
    selectedTemplate = template;
    useOperatorSupport =
      template === "account" && supportConfig?.accountBilling?.mode === "api";
    dialogOpen = true;
  }
</script>

<svelte:head>
  <title>Support & Community - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto max-w-4xl p-6 space-y-6">
  <!-- Header -->
  <div class="flex items-center gap-3">
    <div class="flex h-12 w-12 items-center justify-center rounded-xl bg-primary/10">
      <HeartHandshake class="h-6 w-6 text-primary" />
    </div>
    <div>
      <h1 class="text-2xl font-bold tracking-tight">Support & Community</h1>
      <p class="text-muted-foreground">
        Get help, connect with the community, and share feedback
      </p>
    </div>
  </div>

  <!-- Community Links -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <HeartHandshake class="h-5 w-5" />
        Community
      </CardTitle>
      <CardDescription>Connect with the Nightscout community</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      {#each communityLinks as link}
        <a
          href={link.href}
          target="_blank"
          rel="noopener noreferrer"
          class="flex items-center justify-between p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors"
        >
          <div class="flex items-center gap-4">
            <div
              class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
            >
              <link.icon class="h-5 w-5 text-primary" />
            </div>
            <div>
              <div class="flex items-center gap-2">
                <span class="font-medium">{link.name}</span>
                {#if link.badge}
                  <Badge variant="secondary" class="text-xs">
                    {link.badge}
                  </Badge>
                {/if}
              </div>
              <p class="text-sm text-muted-foreground">{link.description}</p>
            </div>
          </div>
          <ExternalLink class="h-4 w-4 text-muted-foreground" />
        </a>
      {/each}
    </CardContent>
  </Card>

  <!-- Support Options -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <HelpCircle class="h-5 w-5" />
        Get Support
      </CardTitle>
      <CardDescription>Need help? Here's how to reach us</CardDescription>
    </CardHeader>
    <CardContent class="space-y-4">
      <div class="grid gap-4 sm:grid-cols-2">
        {#each supportOptions as option}
          {#if option.template === "account" && supportConfig?.accountBilling?.mode === "redirect"}
            <a
              href={supportConfig.accountBilling.url}
              target="_blank"
              rel="noopener noreferrer"
              class="flex flex-col items-center text-center p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors"
            >
              <div
                class="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 mb-3"
              >
                <ExternalLink class="h-6 w-6 text-primary" />
              </div>
              <span class="font-medium">{supportConfig.accountBilling.label ?? option.name}</span>
              <p class="text-sm text-muted-foreground mt-1">
                {option.description}
              </p>
            </a>
          {:else}
            <button
              class="flex flex-col items-center text-center p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors"
              onclick={() => handleSupportAction(option.template)}
            >
              <div
                class="flex h-12 w-12 items-center justify-center rounded-full bg-primary/10 mb-3"
              >
                <option.icon class="h-6 w-6 text-primary" />
              </div>
              <span class="font-medium">{option.name}</span>
              <p class="text-sm text-muted-foreground mt-1">
                {option.description}
              </p>
            </button>
          {/if}
        {/each}
      </div>

      <div class="flex justify-center pt-2">
        <a
          href="https://discord.gg/xWYz9fFWrj"
          target="_blank"
          rel="noopener noreferrer"
        >
          <Button variant="outline" class="gap-2">
            <Users class="h-4 w-4" />
            Get Help on Discord
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
    <CardContent>
      <div class="flex items-center justify-between">
        <div class="space-y-0.5">
          <p class="text-sm font-medium">Show all tutorials again</p>
          <p class="text-sm text-muted-foreground">
            Reset all guided walkthroughs so they appear as you navigate
          </p>
        </div>
        <Button
          variant="outline"
          class="gap-2"
          onclick={resetTutorials}
          disabled={resettingTutorials}
        >
          <GraduationCap class="h-4 w-4" />
          {resettingTutorials ? "Resetting..." : "Reset Tutorials"}
        </Button>
      </div>
    </CardContent>
  </Card>

  <!-- Share Logs -->
  <Card>
    <CardHeader>
      <CardTitle class="flex items-center gap-2">
        <FileText class="h-5 w-5" />
        Share Diagnostic Logs
      </CardTitle>
      <CardDescription>Export logs to help troubleshoot issues</CardDescription>
    </CardHeader>
    <CardContent class="space-y-6">
      <div class="space-y-4">
        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Include device information</Label>
            <p class="text-sm text-muted-foreground">
              Browser, OS, and screen size
            </p>
          </div>
          <Switch bind:checked={includeDeviceInfo} />
        </div>

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Include recent logs</Label>
            <p class="text-sm text-muted-foreground">
              API calls, errors, and debug information
            </p>
          </div>
          <Switch bind:checked={includeRecentLogs} />
        </div>

        <div class="flex items-center justify-between">
          <div class="space-y-0.5">
            <Label>Include settings</Label>
            <p class="text-sm text-muted-foreground">
              Your configuration (excludes passwords/tokens)
            </p>
          </div>
          <Switch bind:checked={includeSettings} />
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
        <Button variant="outline" class="gap-2" onclick={copyLogs}>
          {#if logsCopied}
            <CheckCircle class="h-4 w-4 text-green-500" />
            Copied!
          {:else}
            <Copy class="h-4 w-4" />
            Copy to Clipboard
          {/if}
        </Button>
        <Button variant="outline" class="gap-2" onclick={downloadLogs}>
          <Download class="h-4 w-4" />
          Download Logs
        </Button>
      </div>

      <Card
        class="border-blue-200 bg-blue-50/50 dark:border-blue-900 dark:bg-blue-950/20"
      >
        <CardContent class="flex items-start gap-3 pt-6">
          <Shield
            class="h-5 w-5 text-blue-600 dark:text-blue-400 shrink-0 mt-0.5"
          />
          <div>
            <p class="font-medium text-blue-900 dark:text-blue-100">
              Privacy Note
            </p>
            <p class="text-sm text-blue-800 dark:text-blue-200">
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
      {#if apiBaseUrl}
        <div class="flex items-center justify-between py-2 border-b">
          <span class="text-muted-foreground">API Endpoint</span>
          <span class="font-mono text-sm">{apiBaseUrl}</span>
        </div>
      {/if}
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">License</span>
        <span>AGPL-3.0</span>
      </div>
      <div class="flex items-center justify-between py-2">
        <span class="text-muted-foreground">API Compatibility</span>
        <Badge variant="secondary">Nightscout v1-v4</Badge>
      </div>

      <Separator class="my-4" />

      <div class="text-center text-sm text-muted-foreground">
        <p>
          Made with <Heart class="h-4 w-4 inline text-red-500" /> by the Nightscout
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
          <Button variant="ghost" size="sm" class="gap-2">
            <Github class="h-4 w-4" />
            Star on GitHub
          </Button>
        </a>
        <a
          href="https://www.nightscoutfoundation.org/donate"
          target="_blank"
          rel="noopener noreferrer"
        >
          <Button variant="ghost" size="sm" class="gap-2">
            <Heart class="h-4 w-4" />
            Donate
          </Button>
        </a>
      </div>
    </CardContent>
  </Card>
</div>

<IssueCreatorDialog bind:open={dialogOpen} template={selectedTemplate} {useOperatorSupport} />
