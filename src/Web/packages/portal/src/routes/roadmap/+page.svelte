<script lang="ts">
    import { Artwork } from "@nocturne/watercolour";
    import { getRoadmapData, type RoadmapMilestone } from "$lib/data/portal";
    import { Button } from "@nocturne/ui/ui/button";
    import MilestoneCard from "$lib/components/MilestoneCard.svelte";
    import {
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
    <div class="pt-20 pb-15">
        <h1 class="text-headline font-bold text-foreground m-0 mb-4">Roadmap</h1>
        <p class="text-lead text-muted-foreground max-w-[560px] m-0 mb-6">
            Nocturne's development milestones, live from GitHub: what's being
            built now, what's next, and what has already shipped.
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
            <AlertCircle class="size-5 text-destructive" aria-hidden="true" />
            <p class="font-semibold text-foreground m-0">Failed to load roadmap</p>
            <p class="m-0 text-sm">{error}</p>
            <Button onclick={loadRoadmap} variant="outline" size="sm">Try again</Button>
        </div>
    {:else if roadmapData.length === 0}
        <div class="flex flex-col items-center justify-center gap-3 py-25 text-muted-foreground text-sm">
            <Artwork artwork="footprints" palette="moss" motion="auto" autoplay="once" class="size-48" />
            <p class="m-0 text-sm">No milestones found.</p>
        </div>
    {:else}
        {#if grouped.inProgress.length > 0}
            <section class="py-16 border-t border-border">
                <h2 class="text-subsection font-bold text-foreground m-0 mb-8">In progress</h2>
                <div class="grid gap-4">
                    {#each grouped.inProgress as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        {#if grouped.upcoming.length > 0}
            <section class="py-16 border-t border-border">
                <h2 class="text-subsection font-bold text-foreground m-0 mb-8">Upcoming</h2>
                <div class="grid gap-4">
                    {#each grouped.upcoming as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        {#if grouped.completed.length > 0}
            <section class="py-16 border-t border-border">
                <h2 class="text-subsection font-bold text-foreground m-0 mb-8">Completed</h2>
                <div class="grid gap-4">
                    {#each grouped.completed as milestone (milestone.id)}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}
    {/if}
</div>
