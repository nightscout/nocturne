<script lang="ts">
    import { getRoadmapData, type RoadmapMilestone } from "$lib/data/portal";
    import { Button } from "@nocturne/ui/ui/button";
    import MilestoneCard from "$lib/components/MilestoneCard.svelte";
    import {
        Milestone,
        GitPullRequest,
        ExternalLink,
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
            roadmapData = await getRoadmapData();
        } catch (e) {
            error = e instanceof Error ? e.message : "Failed to load roadmap";
        } finally {
            loading = false;
        }
    }

    loadRoadmap();

    function getMilestoneStatus(milestone: RoadmapMilestone): "completed" | "in-progress" | "upcoming" {
        if (milestone.state === "closed") return "completed";
        if (milestone.closed_issues > 0) return "in-progress";
        return "upcoming";
    }

    function groupMilestones(milestones: RoadmapMilestone[]) {
        const inProgress = milestones.filter(m => getMilestoneStatus(m) === "in-progress");
        const upcoming = milestones.filter(m => getMilestoneStatus(m) === "upcoming");
        const completed = milestones.filter(m => getMilestoneStatus(m) === "completed");
        return { inProgress, upcoming, completed };
    }

    let grouped = $derived(groupMilestones(roadmapData));
</script>

<div class="max-w-[900px] mx-auto px-6">
    <!-- Page heading -->
    <div class="pt-20 pb-15 border-b border-border">
        <div class="font-mono text-xs tracking-eyebrow uppercase text-muted-foreground mb-4">Roadmap</div>
        <h1 class="text-headline font-bold text-foreground m-0 mb-4">
            What's next.<br />
            <em class="text-glucose-in-range">What's done.</em>
        </h1>
        <p class="text-base leading-relaxed text-muted-foreground max-w-[520px] m-0 mb-6">
            Track Nocturne's development milestones. See what's shipping,
            what's in progress, and what's already shipped.
        </p>
        <div class="flex gap-3 items-center">
            <Button
                href="https://github.com/nightscout/nocturne/issues"
                target="_blank"
                variant="outline"
                size="sm"
            >
                <GitPullRequest class="w-4 h-4" />
                View on GitHub
                <ExternalLink class="w-3 h-3" />
            </Button>
            <Button
                onclick={loadRoadmap}
                variant="ghost"
                size="sm"
                disabled={loading}
            >
                <RefreshCw class="w-4 h-4 {loading ? 'animate-spin' : ''}" />
                Refresh
            </Button>
        </div>
    </div>

    {#if loading}
        <div class="flex flex-col items-center justify-center gap-3 py-25 text-muted-foreground text-sm">
            <Loader2 class="w-6 h-6 animate-spin text-primary" />
            <p>Loading milestones from GitHub&hellip;</p>
        </div>
    {:else if error}
        <div class="flex flex-col items-center justify-center gap-3 py-25 text-muted-foreground text-sm">
            <div
                class="size-10 rounded-full flex items-center justify-center text-destructive bg-destructive/15"
            >
                <AlertCircle class="w-5 h-5" />
            </div>
            <p class="font-semibold text-foreground m-0">Failed to load roadmap</p>
            <p class="m-0 text-sm">{error}</p>
            <Button onclick={loadRoadmap} variant="outline" size="sm">Try again</Button>
        </div>
    {:else if roadmapData.length === 0}
        <div class="flex flex-col items-center justify-center gap-3 py-25 text-muted-foreground text-sm">
            <div class="size-10 rounded-full bg-muted flex items-center justify-center">
                <Milestone class="w-5 h-5 text-muted-foreground" />
            </div>
            <p class="m-0 text-sm">No milestones found.</p>
        </div>
    {:else}
        <!-- In Progress -->
        {#if grouped.inProgress.length > 0}
            <section class="py-16 border-t border-border">
                <div class="mb-8">
                    <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-2.5">01 &middot; In Progress</div>
                    <h2 class="text-subsection font-bold text-foreground m-0">Currently <em class="text-glucose-in-range">building.</em></h2>
                </div>
                <div class="grid gap-4">
                    {#each grouped.inProgress as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Upcoming -->
        {#if grouped.upcoming.length > 0}
            <section class="py-16 border-t border-border">
                <div class="mb-8">
                    <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-2.5">0{grouped.inProgress.length > 0 ? 2 : 1} &middot; Upcoming</div>
                    <h2 class="text-subsection font-bold text-foreground m-0">On the <em class="text-glucose-in-range">horizon.</em></h2>
                </div>
                <div class="grid gap-4">
                    {#each grouped.upcoming as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Completed -->
        {#if grouped.completed.length > 0}
            <section class="py-16 border-t border-border">
                <div class="mb-8">
                    <div class="font-brand text-xs font-bold tracking-eyebrow uppercase text-muted-foreground mb-2.5">0{[grouped.inProgress.length > 0, grouped.upcoming.length > 0].filter(Boolean).length + 1} &middot; Completed</div>
                    <h2 class="text-subsection font-bold text-foreground m-0">Already <em class="text-glucose-in-range">shipped.</em></h2>
                </div>
                <div class="grid gap-4">
                    {#each grouped.completed as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}
    {/if}
</div>
