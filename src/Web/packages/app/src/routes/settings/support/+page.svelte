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
  } from "lucide-svelte";
  import { fetchExternalUrls } from "$lib/data/metadata.remote";
  import { onMount } from "svelte";
  import { getApiClient } from "$lib/api/client";
  import type { ExternalUrls } from "$lib/api";

  let includeDeviceInfo = $state(true);
  let includeRecentLogs = $state(true);
  let includeSettings = $state(false);
  let additionalDetails = $state("");
  let logsCopied = $state(false);

  let serverVersion = $state("Loading...");
  let buildDate = $state("Loading...");
  let commitHash = $state("Loading...");
  let apiCompat = $state("Loading...");
  let externalUrls = $state<ExternalUrls | null>(null);

  onMount(async () => {
    try {
      const api = getApiClient();
      // Cast to any to access new fields before client regeneration
      const meta = (await api.version.getVersion()) as any;
      serverVersion = meta.version || "Unknown";
      buildDate = meta.build || "Dev";
      commitHash = meta.head ? meta.head.substring(0, 7) : "Unknown";
      apiCompat = meta.apiCompatibility || "Nightscout v3 (Legacy)";
    } catch (e) {
      console.error("Failed to load version info", e);
      serverVersion = "Error";
      buildDate = "Error";
      commitHash = "Error";
      apiCompat = "Error";
    }

    try {
      externalUrls = await fetchExternalUrls();
    } catch (e) {
      console.error("Failed to load external URLs", e);
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
      href: externalUrls?.docsBase ?? "#",
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
      action: "report",
    },
    {
      name: "Request a Feature",
      description: "Have an idea? We'd love to hear it",
      icon: HelpCircle,
      action: "feature",
    },
    {
      name: "Get Help",
      description: "Need assistance? Check our FAQ or ask the community",
      icon: Users,
      action: "help",
    },
  ];

  async function copyLogs() {
    // In a real implementation, this would gather actual logs
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
      version: "1.0.0", // Would be actual version
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

  function handleSupportAction(action: string) {
    switch (action) {
      case "report":
        window.open(
          "https://github.com/nightscout/nocturne/issues/new?template=bug_report.md",
          "_blank"
        );
        break;
      case "feature":
        window.open(
          "https://github.com/nightscout/nocturne/issues/new?template=feature_request.md",
          "_blank"
        );
        break;
      case "help":
        window.open("https://discord.gg/nightscout", "_blank");
        break;
    }
  }
</script>

<svelte:head>
  <title>Support & Community - Settings - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-3xl space-y-6">
  <!-- Header -->
  <div>
    <h1 class="text-2xl font-bold tracking-tight">Support & Community</h1>
    <p class="text-muted-foreground">
      Get help, connect with the community, and share feedback
    </p>
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
    <CardContent>
      <div class="grid gap-4 sm:grid-cols-3">
        {#each supportOptions as option}
          <button
            class="flex flex-col items-center text-center p-4 rounded-lg border hover:border-primary/50 hover:bg-accent/50 transition-colors"
            onclick={() => handleSupportAction(option.action)}
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
        {/each}
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
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">Version</span>
        <span class="font-mono">{serverVersion}</span>
      </div>
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">Build</span>
        <span class="font-mono">{buildDate}</span>
      </div>
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">Commit</span>
        <span class="font-mono">{commitHash}</span>
      </div>
      <div class="flex items-center justify-between py-2 border-b">
        <span class="text-muted-foreground">License</span>
        <span>AGPL-3.0</span>
      </div>
      <div class="flex items-center justify-between py-2">
        <span class="text-muted-foreground">API Compatibility</span>
        <Badge variant="secondary">{apiCompat}</Badge>
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
