<script lang="ts">
    import type { RoadmapMilestone, GitHubIssue } from "$lib/data/portal.remote";
    import * as Collapsible from "@nocturne/app/ui/collapsible";
    import {
        Circle,
        CheckCircle2,
        Calendar,
        ExternalLink,
        ChevronDown,
    } from "@lucide/svelte";

    interface Props {
        milestone: RoadmapMilestone;
        status: "completed" | "in-progress" | "upcoming";
    }

    let { milestone, status }: Props = $props();

    function formatDate(dateString: string | null): string {
        if (!dateString) return "";
        return new Date(dateString).toLocaleDateString("en-US", {
            month: "short",
            day: "numeric",
            year: "numeric",
        });
    }

    function getProgressBarColor(status: "completed" | "in-progress" | "upcoming"): string {
        switch (status) {
            case "completed":
                return "bg-green-500";
            case "in-progress":
                return "bg-blue-500";
            case "upcoming":
                return "bg-muted-foreground";
        }
    }

    let totalIssues = $derived(milestone.open_issues + milestone.closed_issues);
</script>

{#snippet issueRow(issue: GitHubIssue)}
    <a
        href={issue.html_url}
        target="_blank"
        rel="noopener noreferrer"
        class="flex items-center gap-3 px-6 py-3 hover:bg-muted/30 transition-colors"
    >
        {#if issue.state === "closed"}
            <CheckCircle2 class="w-4 h-4 text-green-500 flex-shrink-0" />
        {:else}
            <Circle class="w-4 h-4 text-blue-500 flex-shrink-0" />
        {/if}
        <span class="flex-1 text-sm {issue.state === 'closed' ? 'text-muted-foreground line-through' : ''}">
            {issue.title}
        </span>
        <div class="flex items-center gap-2 flex-shrink-0">
            {#each issue.labels.slice(0, 3) as label}
                <span
                    class="px-2 py-0.5 text-xs rounded-full"
                    style="background-color: #{label.color}20; color: #{label.color};"
                >
                    {label.name}
                </span>
            {/each}
            {#if issue.labels.length > 3}
                <span class="text-xs text-muted-foreground">
                    +{issue.labels.length - 3}
                </span>
            {/if}
        </div>
        <span class="text-xs text-muted-foreground">#{issue.number}</span>
    </a>
{/snippet}

<Collapsible.Root>
    <div class="rounded-xl border border-border/60 bg-card/50 overflow-hidden">
        <Collapsible.Trigger class="w-full text-left">
            <div class="p-6 hover:bg-muted/30 transition-colors cursor-pointer">
                <div class="flex items-start justify-between gap-4 mb-4">
                    <div class="flex-1">
                        <div class="flex items-center gap-2 mb-2">
                            {#if status === "completed"}
                                <CheckCircle2 class="w-5 h-5 text-green-500" />
                            {:else if status === "in-progress"}
                                <Circle class="w-5 h-5 text-blue-500" />
                            {:else}
                                <Circle class="w-5 h-5 text-muted-foreground" />
                            {/if}
                            <h3 class="text-lg font-semibold">{milestone.title}</h3>
                        </div>
                        {#if milestone.description}
                            <p class="text-sm text-muted-foreground mb-3">
                                {milestone.description}
                            </p>
                        {/if}
                        <div class="flex flex-wrap items-center gap-4 text-sm text-muted-foreground">
                            {#if milestone.due_on}
                                <span class="flex items-center gap-1">
                                    <Calendar class="w-4 h-4" />
                                    {status === "completed" ? "Completed" : "Due"}: {formatDate(milestone.due_on)}
                                </span>
                            {/if}
                            <span>
                                {milestone.closed_issues} / {totalIssues} issues completed
                            </span>
                        </div>
                    </div>
                    <div class="flex items-center gap-3">
                        <a
                            href={milestone.html_url}
                            target="_blank"
                            rel="noopener noreferrer"
                            class="p-2 rounded-md hover:bg-muted transition-colors"
                            onclick={(e: MouseEvent) => e.stopPropagation()}
                        >
                            <ExternalLink class="w-4 h-4 text-muted-foreground" />
                        </a>
                        <ChevronDown class="w-5 h-5 text-muted-foreground transition-transform [[data-state=open]_&]:rotate-180" />
                    </div>
                </div>

                <!-- Progress Bar -->
                <div class="h-2 bg-muted rounded-full overflow-hidden">
                    <div
                        class="h-full transition-all duration-500 {getProgressBarColor(status)}"
                        style="width: {milestone.progress}%"
                    ></div>
                </div>
            </div>
        </Collapsible.Trigger>

        <Collapsible.Content>
            {#if milestone.issues.length > 0}
                <div class="border-t border-border/60 divide-y divide-border/40">
                    {#each milestone.issues as issue}
                        {@render issueRow(issue)}
                    {/each}
                </div>
            {:else}
                <div class="border-t border-border/60 p-4 text-center text-sm text-muted-foreground">
                    No issues in this milestone yet
                </div>
            {/if}
        </Collapsible.Content>
    </div>
</Collapsible.Root>
