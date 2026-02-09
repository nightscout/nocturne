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
  import {
    Bell,
    History,
    Clock,
    Check,
    AlertTriangle,
    AlertCircle,
    Timer,
    Info,
    Loader2,
    Settings2,
    ChevronDown,
    Shield,
    KeyRound,
    ExternalLink,
  } from "lucide-svelte";
  import { cn } from "$lib/utils";
  import { getRealtimeStore } from "$lib/stores/realtime-store.svelte";
  import { getAuthStore } from "$lib/stores/auth-store.svelte";
  import * as trackersRemote from "$lib/data/generated/trackers.generated.remote";
  import * as adminRemote from "$lib/data/generated/localauths.generated.remote";
  import {
    CompletionReason,
    type TrackerInstanceDto,
    type PasswordResetRequestDto,
  } from "$api";
  import * as Collapsible from "$lib/components/ui/collapsible";
  import { goto } from "$app/navigation";
  import { onMount, untrack } from "svelte";

  // Get the realtime store for reactive tracker data
  const realtimeStore = getRealtimeStore();
  const authStore = getAuthStore();

  // Check if user is an admin
  const isAdmin = $derived(authStore.hasRole("admin"));

  // State
  let loading = $state(true);
  let error = $state<string | null>(null);
  let historyInstances = $state<TrackerInstanceDto[]>([]);
  let pendingResets = $state<PasswordResetRequestDto[]>([]);

  // Use active tracker notifications from realtime store
  const trackerNotifications = $derived(realtimeStore.trackerNotifications);

  // Count by level for display
  const urgentCount = $derived(
    trackerNotifications.filter((n) => n.level === "urgent").length
  );
  const hazardCount = $derived(
    trackerNotifications.filter((n) => n.level === "hazard").length
  );
  const warnCount = $derived(
    trackerNotifications.filter((n) => n.level === "warn").length
  );

  // Completion reason labels
  const completionReasonLabels: Record<CompletionReason, string> = {
    [CompletionReason.Completed]: "Completed",
    [CompletionReason.Expired]: "Expired",
    [CompletionReason.Other]: "Other",
    [CompletionReason.Failed]: "Failed",
    [CompletionReason.FellOff]: "Fell Off",
    [CompletionReason.ReplacedEarly]: "Replaced Early",
    [CompletionReason.Empty]: "Empty",
    [CompletionReason.Refilled]: "Refilled",
    [CompletionReason.Attended]: "Attended",
    [CompletionReason.Rescheduled]: "Rescheduled",
    [CompletionReason.Cancelled]: "Cancelled",
    [CompletionReason.Missed]: "Missed",
  };

  // Load data
  async function loadData() {
    loading = true;
    error = null;
    try {
      const [history, resets] = await Promise.all([
        trackersRemote.getInstanceHistory(undefined),
        isAdmin
          ? adminRemote.getPendingPasswordResets()
          : Promise.resolve(null),
      ]);
      historyInstances = history || [];
      pendingResets = resets?.requests ?? [];
    } catch (err) {
      console.error("Failed to load notification data:", err);
      error = "Failed to load notification data";
    } finally {
      loading = false;
    }
  }

  // Initial load on mount (not in $effect to avoid unnecessary re-runs)
  onMount(() => {
    loadData();
  });

  // Reload when password reset request counter changes (via SignalR)
  // Use untrack to avoid creating a dependency cycle with lastResetCount
  let lastResetCount = $state<number | undefined>(undefined);

  $effect(() => {
    const currentCount = realtimeStore.passwordResetRequestCount;
    // Read previous count without creating a subscription
    const previousCount = untrack(() => lastResetCount);

    // Only reload if the count actually changed (not on initial mount)
    if (previousCount !== undefined && currentCount !== previousCount) {
      // Capture isAdmin value here to avoid reactive re-runs
      const shouldReload = authStore.hasRole("admin");
      if (shouldReload) {
        loadData();
      }
    }

    // Write without creating a subscription
    untrack(() => {
      lastResetCount = currentCount;
    });
  });

  // Format age
  function formatAge(hours: number): string {
    if (hours < 1) return `${Math.floor(hours * 60)}m`;
    if (hours < 24) return `${Math.floor(hours)}h`;
    const days = Math.floor(hours / 24);
    const h = Math.floor(hours % 24);
    return h > 0 ? `${days}d ${h}h` : `${days}d`;
  }

  // Format date
  function formatDate(dateStr: Date | undefined): string {
    if (!dateStr) return "";
    return new Date(dateStr).toLocaleDateString(undefined, {
      month: "short",
      day: "numeric",
      hour: "2-digit",
      minute: "2-digit",
    });
  }

  // Get level icon
  function getLevelIcon(level: string) {
    switch (level) {
      case "urgent":
        return AlertCircle;
      case "hazard":
        return AlertTriangle;
      case "warn":
        return Timer;
      default:
        return Info;
    }
  }

  // Get level colors
  function getLevelClass(level: string): string {
    switch (level) {
      case "urgent":
        return "text-red-500 bg-red-500/10 border-red-500/20";
      case "hazard":
        return "text-orange-500 bg-orange-500/10 border-orange-500/20";
      case "warn":
        return "text-yellow-500 bg-yellow-500/10 border-yellow-500/20";
      default:
        return "text-muted-foreground bg-muted border-border";
    }
  }

  // Build notification message
  function buildMessage(
    instance: TrackerInstanceDto & { level: string }
  ): string {
    const def = realtimeStore.trackerDefinitions.find(
      (d) => d.id === instance.definitionId
    );
    if (!def) return "Tracker active";

    // Find threshold hours for the level from notificationThresholds
    const threshold = def.notificationThresholds?.find(
      (t) => t.urgency?.toLowerCase() === instance.level
    );
    const thresholdHours = threshold?.hours ?? def.lifespanHours;

    switch (instance.level) {
      case "urgent":
        return `Exceeded ${thresholdHours ?? "?"}h - change urgently!`;
      case "hazard":
        return `Exceeded ${thresholdHours ?? "?"}h - change soon`;
      case "warn":
        return `Approaching ${thresholdHours ?? def.lifespanHours ?? "?"}h limit`;
      default:
        return `Active for ${Math.floor(instance.ageHours ?? 0)}h`;
    }
  }

  // Group history by date
  const groupedHistory = $derived.by(() => {
    const groups: Record<string, TrackerInstanceDto[]> = {};

    for (const instance of historyInstances) {
      const date = new Date(
        instance.completedAt ?? instance.startedAt ?? new Date()
      );
      const key = date.toLocaleDateString(undefined, {
        weekday: "long",
        year: "numeric",
        month: "long",
        day: "numeric",
      });

      if (!groups[key]) {
        groups[key] = [];
      }
      groups[key].push(instance);
    }

    return groups;
  });

  // Collapsible state for history groups
  let expandedGroups = $state<Record<string, boolean>>({});

  // Initialize all groups - first one expanded, rest collapsed
  // Use $effect.pre with untrack to avoid read/write cycle
  $effect.pre(() => {
    const keys = Object.keys(groupedHistory);
    if (keys.length === 0) return;

    // Check if we've already initialized any groups from this set
    const hasAnyExpanded = untrack(() =>
      Object.keys(expandedGroups).some((k) => keys.includes(k))
    );
    if (hasAnyExpanded) return;

    // Initialize without creating reactive dependencies on expandedGroups
    untrack(() => {
      for (let i = 0; i < keys.length; i++) {
        if (expandedGroups[keys[i]] === undefined) {
          expandedGroups[keys[i]] = i === 0; // First group expanded
        }
      }
    });
  });

  // Helper to toggle group
  function toggleGroup(date: string) {
    expandedGroups[date] = !expandedGroups[date];
  }

  // Helper to check if group is expanded
  function isExpanded(date: string): boolean {
    return expandedGroups[date] ?? false;
  }

  // Total active count for header
  const totalActiveCount = $derived(
    trackerNotifications.length + pendingResets.length
  );
</script>

<svelte:head>
  <title>Notifications - Nocturne</title>
</svelte:head>

<div class="container mx-auto p-6 max-w-4xl">
  <!-- Header -->
  <div class="mb-8">
    <div class="flex items-center gap-3 mb-2">
      <div
        class="flex h-10 w-10 items-center justify-center rounded-lg bg-primary/10"
      >
        <Bell class="h-5 w-5 text-primary" />
      </div>
      <div class="flex-1">
        <div class="flex items-center gap-3">
          <h1 class="text-3xl font-bold tracking-tight">Notifications</h1>
          {#if totalActiveCount > 0}
            <Badge variant="destructive">{totalActiveCount} active</Badge>
          {/if}
        </div>
        <p class="text-muted-foreground">
          View and manage all notifications and alerts
        </p>
      </div>
    </div>
  </div>

  {#if loading}
    <div class="flex items-center justify-center py-12">
      <Loader2 class="h-8 w-8 animate-spin text-muted-foreground" />
    </div>
  {:else if error}
    <Card class="border-destructive">
      <CardContent class="py-6 text-center">
        <AlertTriangle class="h-8 w-8 text-destructive mx-auto mb-2" />
        <p class="text-destructive">{error}</p>
        <Button variant="outline" class="mt-4" onclick={loadData}>Retry</Button>
      </CardContent>
    </Card>
  {:else}
    <div class="space-y-6">
      <!-- Admin Notifications Section (for admins only) -->
      {#if isAdmin && pendingResets.length > 0}
        <Card class="border-destructive/50">
          <CardHeader class="flex flex-row items-center justify-between">
            <div>
              <CardTitle class="flex items-center gap-2">
                <Shield class="h-5 w-5 text-destructive" />
                Admin Actions Required
              </CardTitle>
              <CardDescription>
                Administrative tasks requiring your attention
              </CardDescription>
            </div>
            <Badge variant="destructive">{pendingResets.length}</Badge>
          </CardHeader>
          <CardContent>
            <div class="space-y-3">
              {#each pendingResets as request (request.id)}
                <div
                  class="flex items-start gap-3 p-4 rounded-lg border text-red-500 bg-red-500/10 border-red-500/20"
                >
                  <div class="flex-shrink-0 mt-0.5">
                    <KeyRound class="h-5 w-5" />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center justify-between gap-2">
                      <span class="font-medium">Password Reset Request</span>
                      <span class="text-xs opacity-75">
                        {request.createdAt ? formatDate(request.createdAt) : ""}
                      </span>
                    </div>
                    <p class="text-sm mt-1 opacity-75">
                      {request.displayName ?? request.email}
                    </p>
                    <div class="flex items-center gap-2 mt-2">
                      <Button
                        variant="outline"
                        size="sm"
                        onclick={() =>
                          goto("/settings/admin?tab=password-resets")}
                      >
                        <ExternalLink class="h-3 w-3 mr-1" />
                        Handle Request
                      </Button>
                    </div>
                  </div>
                </div>
              {/each}
            </div>
          </CardContent>
        </Card>
      {/if}

      <!-- Tracker Notifications Section -->
      <Card>
        <CardHeader class="flex flex-row items-center justify-between">
          <div>
            <CardTitle class="flex items-center gap-2">
              <Timer class="h-5 w-5 text-orange-500" />
              Tracker Alerts
            </CardTitle>
            <CardDescription>
              Consumable and reminder notifications
            </CardDescription>
          </div>
          <div class="flex items-center gap-2">
            {#if urgentCount > 0}
              <Badge variant="destructive">{urgentCount} urgent</Badge>
            {/if}
            {#if hazardCount > 0}
              <Badge class="bg-orange-500 text-white hover:bg-orange-600">
                {hazardCount} hazard
              </Badge>
            {/if}
            {#if warnCount > 0}
              <Badge variant="secondary">{warnCount} warning</Badge>
            {/if}
            <a href="/settings/trackers">
              <Button variant="outline" size="sm">
                <Settings2 class="h-4 w-4 mr-2" />
                Manage
              </Button>
            </a>
          </div>
        </CardHeader>
        <CardContent>
          {#if trackerNotifications.length === 0}
            <div class="text-center py-8 text-muted-foreground">
              <Check class="h-12 w-12 mx-auto mb-3 text-green-500 opacity-50" />
              <p>All caught up! No active tracker alerts.</p>
            </div>
          {:else}
            <div class="space-y-3">
              {#each trackerNotifications as notification (notification.id)}
                {@const LevelIcon = getLevelIcon(notification.level)}
                <div
                  class={cn(
                    "flex items-start gap-3 p-4 rounded-lg border",
                    getLevelClass(notification.level)
                  )}
                >
                  <div class="flex-shrink-0 mt-0.5">
                    <LevelIcon class="h-5 w-5" />
                  </div>
                  <div class="flex-1 min-w-0">
                    <div class="flex items-center justify-between gap-2">
                      <span class="font-medium">
                        {notification.definitionName}
                      </span>
                      <Badge variant="outline" class="ml-auto">
                        {formatAge(notification.ageHours ?? 0)}
                      </Badge>
                    </div>
                    <p class="text-sm mt-1 opacity-75">
                      {buildMessage(notification)}
                    </p>
                    {#if notification.startedAt}
                      <p class="text-xs mt-1 opacity-50">
                        Started {formatDate(notification.startedAt)}
                      </p>
                    {/if}
                  </div>
                </div>
              {/each}
            </div>
          {/if}
        </CardContent>
      </Card>

      <!-- History Section -->
      <Card>
        <CardHeader>
          <CardTitle class="flex items-center gap-2">
            <History class="h-5 w-5 text-muted-foreground" />
            Notification History
          </CardTitle>
          <CardDescription>
            Past tracker notifications and completions
          </CardDescription>
        </CardHeader>
        <CardContent>
          {#if historyInstances.length === 0}
            <div class="text-center py-8 text-muted-foreground">
              <History class="h-12 w-12 mx-auto mb-3 opacity-50" />
              <p>No history yet</p>
              <p class="text-sm">Completed trackers will appear here</p>
            </div>
          {:else}
            <div class="space-y-4">
              {#each Object.entries(groupedHistory) as [date, instances]}
                <Collapsible.Root
                  open={isExpanded(date)}
                  onOpenChange={() => toggleGroup(date)}
                  class="border rounded-lg"
                >
                  <Collapsible.Trigger
                    class="flex items-center justify-between w-full p-3 hover:bg-muted/50 rounded-t-lg"
                  >
                    <div class="flex items-center gap-2">
                      <Clock class="h-4 w-4 text-muted-foreground" />
                      <span class="font-medium">{date}</span>
                      <Badge variant="secondary" class="ml-2">
                        {instances.length}
                      </Badge>
                    </div>
                    <ChevronDown
                      class={cn(
                        "h-4 w-4 transition-transform",
                        isExpanded(date) && "rotate-180"
                      )}
                    />
                  </Collapsible.Trigger>
                  <Collapsible.Content class="border-t p-3 space-y-2">
                    {#each instances as instance}
                      <div
                        class="flex items-center justify-between p-3 rounded-lg bg-muted/30"
                      >
                        <div class="flex items-center gap-3">
                          <Check class="h-4 w-4 text-green-500" />
                          <div>
                            <div class="font-medium">
                              {instance.definitionName}
                            </div>
                            <div class="text-sm text-muted-foreground">
                              Duration: {formatAge(instance.ageHours ?? 0)} ·
                              {completionReasonLabels[
                                instance.completionReason ??
                                  CompletionReason.Completed
                              ]}
                              {#if instance.completionNotes}
                                · {instance.completionNotes}
                              {/if}
                            </div>
                          </div>
                        </div>
                        <div class="text-sm text-muted-foreground">
                          {formatDate(
                            instance.completedAt ?? instance.startedAt
                          )}
                        </div>
                      </div>
                    {/each}
                  </Collapsible.Content>
                </Collapsible.Root>
              {/each}
            </div>
          {/if}
        </CardContent>
      </Card>
    </div>
  {/if}
</div>
