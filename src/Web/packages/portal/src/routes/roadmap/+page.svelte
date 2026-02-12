<script lang="ts">
    import { getRoadmapData, type RoadmapMilestone } from "$lib/data/portal.remote";
    import { Button } from "@nocturne/app/ui/button";
    import MilestoneCard from "$lib/components/MilestoneCard.svelte";
    import {
        Milestone,
        Circle,
        CheckCircle2,
        Calendar,
        ExternalLink,
        GitPullRequest,
        AlertCircle,
        Loader2,
        RefreshCw,
    } from "@lucide/svelte";

    let roadmapData = $state<RoadmapMilestone[]>([]);
    let loading = $state(true);
    let error = $state<string | null>(null);

    async function loadRoadmap() {
        loading = true;
        error = null;
        try {
            roadmapData = await getRoadmapData({});
        } catch (e) {
            error = e instanceof Error ? e.message : "Failed to load roadmap";
        } finally {
            loading = false;
        }
    }

    // Initial load
    loadRoadmap();

    function getMilestoneStatus(milestone: RoadmapMilestone): "completed" | "in-progress" | "upcoming" {
        if (milestone.state === "closed") return "completed";
        if (milestone.closed_issues > 0) return "in-progress";
        return "upcoming";
    }

    // Group milestones by status
    function groupMilestones(milestones: RoadmapMilestone[]) {
        const inProgress = milestones.filter(m => getMilestoneStatus(m) === "in-progress");
        const upcoming = milestones.filter(m => getMilestoneStatus(m) === "upcoming");
        const completed = milestones.filter(m => getMilestoneStatus(m) === "completed");
        return { inProgress, upcoming, completed };
    }

    let grouped = $derived(groupMilestones(roadmapData));
</script>

<div class="container mx-auto px-4 py-12">
    <!-- Hero -->
    <div class="text-center mb-12">
        <h1 class="text-4xl md:text-5xl font-bold tracking-tight mb-4">
            Roadmap
        </h1>
        <p class="text-lg text-muted-foreground max-w-2xl mx-auto mb-6">
            Track the development progress of Nocturne. See what we're working on,
            what's coming next, and what's been completed.
        </p>
        <div class="flex justify-center gap-4">
            <Button
                href="https://github.com/nightscout/nocturne/issues"
                target="_blank"
                variant="outline"
                size="sm"
                class="gap-2"
            >
                <GitPullRequest class="w-4 h-4" />
                View on GitHub
                <ExternalLink class="w-3 h-3" />
            </Button>
            <Button
                onclick={loadRoadmap}
                variant="ghost"
                size="sm"
                class="gap-2"
                disabled={loading}
            >
                <RefreshCw class="w-4 h-4 {loading ? 'animate-spin' : ''}" />
                Refresh
            </Button>
        </div>
    </div>

    {#if loading}
        <div class="flex flex-col items-center justify-center py-20">
            <Loader2 class="w-8 h-8 animate-spin text-primary mb-4" />
            <p class="text-muted-foreground">Loading roadmap from GitHub...</p>
        </div>
    {:else if error}
        <div class="flex flex-col items-center justify-center py-20">
            <div class="w-16 h-16 rounded-full bg-destructive/15 flex items-center justify-center mb-4">
                <AlertCircle class="w-8 h-8 text-destructive" />
            </div>
            <p class="text-destructive font-medium mb-2">Failed to load roadmap</p>
            <p class="text-sm text-muted-foreground mb-4">{error}</p>
            <Button onclick={loadRoadmap} variant="outline" size="sm">
                Try Again
            </Button>
        </div>
    {:else if roadmapData.length === 0}
        <div class="flex flex-col items-center justify-center py-20">
            <div class="w-16 h-16 rounded-full bg-muted flex items-center justify-center mb-4">
                <Milestone class="w-8 h-8 text-muted-foreground" />
            </div>
            <p class="text-muted-foreground">No milestones found</p>
        </div>
    {:else}
        <!-- In Progress -->
        {#if grouped.inProgress.length > 0}
            <section class="mb-12">
                <h2 class="text-2xl font-bold mb-6 flex items-center gap-2">
                    <div class="w-8 h-8 rounded-lg bg-blue-500/15 flex items-center justify-center">
                        <Circle class="w-4 h-4 text-blue-500" />
                    </div>
                    In Progress
                </h2>
                <div class="grid gap-6">
                    {#each grouped.inProgress as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Upcoming -->
        {#if grouped.upcoming.length > 0}
            <section class="mb-12">
                <h2 class="text-2xl font-bold mb-6 flex items-center gap-2">
                    <div class="w-8 h-8 rounded-lg bg-muted flex items-center justify-center">
                        <Calendar class="w-4 h-4 text-muted-foreground" />
                    </div>
                    Upcoming
                </h2>
                <div class="grid gap-6">
                    {#each grouped.upcoming as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Completed -->
        {#if grouped.completed.length > 0}
            <section class="mb-12">
                <h2 class="text-2xl font-bold mb-6 flex items-center gap-2">
                    <div class="w-8 h-8 rounded-lg bg-green-500/15 flex items-center justify-center">
                        <CheckCircle2 class="w-4 h-4 text-green-500" />
                    </div>
                    Completed
                </h2>
                <div class="grid gap-6">
                    {#each grouped.completed as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}
    {/if}
</div>
