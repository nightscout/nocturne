<script lang="ts">
    import { getRoadmapData, type RoadmapMilestone } from "$lib/data/portal.remote";
    import { Button } from "@nocturne/ui/ui/button";
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

<div class="roadmap-wrap">
    <!-- Page heading -->
    <div class="page-hero">
        <div class="page-label">Roadmap</div>
        <h1>
            What's next.<br />
            <em>What's done.</em>
        </h1>
        <p>
            Track Nocturne's development milestones. See what's shipping,
            what's in progress, and what's already shipped.
        </p>
        <div class="hero-actions">
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
        <div class="state-view">
            <Loader2 class="w-6 h-6 animate-spin text-primary" />
            <p>Loading milestones from GitHub&hellip;</p>
        </div>
    {:else if error}
        <div class="state-view">
            <div class="state-icon state-icon--error">
                <AlertCircle class="w-5 h-5" />
            </div>
            <p class="state-title">Failed to load roadmap</p>
            <p class="state-sub">{error}</p>
            <Button onclick={loadRoadmap} variant="outline" size="sm">Try again</Button>
        </div>
    {:else if roadmapData.length === 0}
        <div class="state-view">
            <div class="state-icon">
                <Milestone class="w-5 h-5 text-muted-foreground" />
            </div>
            <p class="state-sub">No milestones found.</p>
        </div>
    {:else}
        <!-- In Progress -->
        {#if grouped.inProgress.length > 0}
            <section class="roadmap-section">
                <div class="page-section-head">
                    <div class="page-section-label">&#167; 01 &mdash; In Progress</div>
                    <h2>Currently <em>building.</em></h2>
                </div>
                <div class="milestone-grid">
                    {#each grouped.inProgress as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Upcoming -->
        {#if grouped.upcoming.length > 0}
            <section class="roadmap-section">
                <div class="page-section-head">
                    <div class="page-section-label">&#167; 0{grouped.inProgress.length > 0 ? 2 : 1} &mdash; Upcoming</div>
                    <h2>On the <em>horizon.</em></h2>
                </div>
                <div class="milestone-grid">
                    {#each grouped.upcoming as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}

        <!-- Completed -->
        {#if grouped.completed.length > 0}
            <section class="roadmap-section">
                <div class="page-section-head">
                    <div class="page-section-label">&#167; 0{[grouped.inProgress.length > 0, grouped.upcoming.length > 0].filter(Boolean).length + 1} &mdash; Completed</div>
                    <h2>Already <em>shipped.</em></h2>
                </div>
                <div class="milestone-grid">
                    {#each grouped.completed as milestone}
                        <MilestoneCard {milestone} status={getMilestoneStatus(milestone)} />
                    {/each}
                </div>
            </section>
        {/if}
    {/if}
</div>

<style>
    .roadmap-wrap {
        max-width: 900px;
        margin: 0 auto;
        padding: 0 24px;
    }

    .page-hero {
        padding: 80px 0 60px;
        border-bottom: 1px solid var(--border);
    }

    .page-label {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted-foreground);
        margin-bottom: 16px;
    }

    .page-hero h1 {
        font-size: clamp(2rem, 4vw, 3.2rem);
        font-weight: 700;
        line-height: 1.15;
        letter-spacing: -0.025em;
        color: var(--foreground);
        margin: 0 0 16px;
    }

    .page-hero h1 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .page-hero p {
        font-size: 1rem;
        line-height: 1.65;
        color: var(--muted-foreground);
        max-width: 520px;
        margin: 0 0 24px;
    }

    .hero-actions {
        display: flex;
        gap: 12px;
        align-items: center;
    }

    /* Loading / error / empty states */
    .state-view {
        display: flex;
        flex-direction: column;
        align-items: center;
        justify-content: center;
        gap: 12px;
        padding: 100px 0;
        color: var(--muted-foreground);
        font-size: 0.9375rem;
    }

    .state-icon {
        width: 40px;
        height: 40px;
        border-radius: 50%;
        background: var(--muted);
        display: flex;
        align-items: center;
        justify-content: center;
    }

    .state-icon--error {
        background: color-mix(in oklab, var(--destructive) 15%, transparent);
        color: var(--destructive);
    }

    .state-title {
        font-weight: 600;
        color: var(--foreground);
        margin: 0;
    }

    .state-sub {
        margin: 0;
        font-size: 0.875rem;
    }

    /* Sections */
    .roadmap-section {
        padding: 64px 0;
        border-top: 1px solid var(--border);
    }

    .page-section-head {
        margin-bottom: 32px;
    }

    .page-section-label {
        font-family: ui-monospace, "SF Mono", Menlo, monospace;
        font-size: 11px;
        letter-spacing: 0.14em;
        text-transform: uppercase;
        color: var(--muted-foreground);
        margin-bottom: 10px;
    }

    .page-section-head h2 {
        font-size: clamp(1.4rem, 2.5vw, 2rem);
        font-weight: 700;
        line-height: 1.2;
        letter-spacing: -0.02em;
        color: var(--foreground);
        margin: 0;
    }

    .page-section-head h2 em {
        font-style: italic;
        color: var(--glucose-in-range);
    }

    .milestone-grid {
        display: grid;
        gap: 16px;
    }
</style>
