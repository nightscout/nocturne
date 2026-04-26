<script lang="ts">
    import { getChangelog, type ChangelogRelease } from "$lib/data/portal.remote";
    import { Button } from "@nocturne/ui/ui/button";
    import { marked } from "marked";
    import {
        Tag,
        ExternalLink,
        Loader2,
        AlertCircle,
        RefreshCw,
        ChevronDown,
        Github,
    } from "@lucide/svelte";

    let releases = $state<ChangelogRelease[]>([]);
    let loading = $state(true);
    let loadingMore = $state(false);
    let error = $state<string | null>(null);
    let currentPage = $state(1);
    let hasMore = $state(true);
    const perPage = 30;

    async function loadChangelog(append = false) {
        if (append) {
            loadingMore = true;
        } else {
            loading = true;
            releases = [];
            currentPage = 1;
        }
        error = null;

        try {
            const page = append ? currentPage + 1 : 1;
            const data = await getChangelog({ page, per_page: perPage });

            if (append) {
                releases = [...releases, ...data];
                currentPage = page;
            } else {
                releases = data;
                currentPage = 1;
            }

            hasMore = data.length >= perPage;
        } catch (e) {
            error = e instanceof Error ? e.message : "Failed to load changelog";
        } finally {
            loading = false;
            loadingMore = false;
        }
    }

    marked.use({ breaks: true });

    function renderMarkdown(body: string | null): string {
        if (!body) return "";
        return marked.parse(body, { async: false }) as string;
    }

    function formatDate(dateStr: string | null): string {
        if (!dateStr) return "";
        return new Date(dateStr).toLocaleDateString("en-US", {
            year: "numeric",
            month: "short",
            day: "numeric",
        });
    }

    function formatMonthYear(dateStr: string | null): string {
        if (!dateStr) return "";
        return new Date(dateStr).toLocaleDateString("en-US", {
            year: "numeric",
            month: "short",
        });
    }

    // Initial load
    loadChangelog();
</script>

<svelte:head>
    <title>Changelog - Nocturne</title>
    <meta name="description" content="All the latest updates, improvements, and fixes to Nocturne." />
</svelte:head>

<div class="container mx-auto px-4 py-12">
    <!-- Hero -->
    <div class="text-center mb-12">
        <h1 class="text-4xl md:text-5xl font-bold tracking-tight mb-4">
            Changelog
        </h1>
        <p class="text-lg text-muted-foreground max-w-2xl mx-auto mb-6">
            All the latest updates, improvements, and fixes to Nocturne.
        </p>
        <div class="flex justify-center gap-4">
            <Button
                href="https://github.com/nightscout/nocturne/releases"
                target="_blank"
                variant="outline"
                size="sm"
                class="gap-2"
            >
                <Github class="w-4 h-4" />
                View on GitHub
                <ExternalLink class="w-3 h-3" />
            </Button>
            <Button
                onclick={() => loadChangelog()}
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
            <p class="text-muted-foreground">Loading changelog from GitHub...</p>
        </div>
    {:else if error}
        <div class="flex flex-col items-center justify-center py-20">
            <div class="w-16 h-16 rounded-full bg-destructive/15 flex items-center justify-center mb-4">
                <AlertCircle class="w-8 h-8 text-destructive" />
            </div>
            <p class="text-destructive font-medium mb-2">Failed to load changelog</p>
            <p class="text-sm text-muted-foreground mb-4">{error}</p>
            <Button onclick={() => loadChangelog()} variant="outline" size="sm">
                Try Again
            </Button>
        </div>
    {:else if releases.length === 0}
        <div class="flex flex-col items-center justify-center py-20">
            <div class="w-16 h-16 rounded-full bg-muted flex items-center justify-center mb-4">
                <Tag class="w-8 h-8 text-muted-foreground" />
            </div>
            <p class="text-muted-foreground">No releases found</p>
        </div>
    {:else}
        <!-- Releases -->
        <div class="max-w-4xl mx-auto">
            {#each releases as release, i (release.id)}
                <div class="grid grid-cols-1 md:grid-cols-[180px_1fr] gap-4 md:gap-8">
                    <!-- Sticky version label (left column) -->
                    <div class="md:sticky md:top-20 md:self-start">
                        <div class="flex md:flex-col items-baseline md:items-start gap-2 md:gap-1 mb-2 md:mb-0">
                            <a
                                href={release.html_url}
                                target="_blank"
                                rel="noopener noreferrer"
                                class="text-lg font-semibold font-mono hover:text-primary transition-colors"
                            >
                                {release.tag_name}
                            </a>
                            <span class="text-sm text-muted-foreground">
                                {formatMonthYear(release.published_at)}
                            </span>
                            {#if release.prerelease}
                                <span class="text-xs font-medium px-2 py-0.5 rounded-full bg-yellow-500/15 text-yellow-600">
                                    Pre-release
                                </span>
                            {/if}
                        </div>
                    </div>

                    <!-- Release content (right column) -->
                    <div class="pb-10 {i < releases.length - 1 ? 'border-b border-border/40 mb-10' : ''}">
                        <h2 class="text-xl font-semibold mb-2">
                            {release.name || release.tag_name}
                        </h2>
                        <div class="flex items-center gap-3 text-sm text-muted-foreground mb-4">
                            <span>{formatDate(release.published_at)}</span>
                            {#if release.author}
                                <span class="flex items-center gap-1.5">
                                    <img
                                        src={release.author.avatar_url}
                                        alt={release.author.login}
                                        class="w-5 h-5 rounded-full"
                                    />
                                    <a
                                        href={release.author.html_url}
                                        target="_blank"
                                        rel="noopener noreferrer"
                                        class="hover:text-foreground transition-colors"
                                    >
                                        {release.author.login}
                                    </a>
                                </span>
                            {/if}
                        </div>

                        {#if release.body}
                            <div class="prose prose-sm dark:prose-invert max-w-none">
                                {@html renderMarkdown(release.body)}
                            </div>
                        {/if}

                        <div class="mt-4">
                            <Button
                                href={release.html_url}
                                target="_blank"
                                variant="ghost"
                                size="sm"
                                class="gap-1.5 text-muted-foreground"
                            >
                                View on GitHub
                                <ExternalLink class="w-3 h-3" />
                            </Button>
                        </div>
                    </div>
                </div>
            {/each}
        </div>

        <!-- Load More -->
        {#if hasMore}
            <div class="flex justify-center mt-8">
                <Button
                    onclick={() => loadChangelog(true)}
                    variant="outline"
                    disabled={loadingMore}
                    class="gap-2"
                >
                    {#if loadingMore}
                        <Loader2 class="w-4 h-4 animate-spin" />
                        Loading...
                    {:else}
                        <ChevronDown class="w-4 h-4" />
                        Load more releases
                    {/if}
                </Button>
            </div>
        {/if}
    {/if}
</div>
