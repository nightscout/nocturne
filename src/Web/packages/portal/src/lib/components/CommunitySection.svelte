<script lang="ts">
    import { Button } from "@nocturne/ui/ui/button";
    import { Star, GitFork, Users, Tag } from "@lucide/svelte";
    import type { GitHubContributor } from "$lib/data/portal.remote";

    interface Props {
        stars: number;
        forks: number;
        contributors: GitHubContributor[];
        latestRelease: string | null;
    }

    let { stars, forks, contributors, latestRelease }: Props = $props();
</script>

<section class="py-20 bg-muted/30">
    <div class="container mx-auto px-4">
        <div class="text-center mb-12">
            <h2 class="text-3xl font-bold mb-4">Built by the Community</h2>
            <p class="text-muted-foreground max-w-2xl mx-auto">
                Nocturne is open source and shaped by contributors from the
                diabetes community worldwide.
            </p>
        </div>

        <!-- Stats Row -->
        <div
            class="flex flex-wrap justify-center gap-6 md:gap-10 mb-12 max-w-3xl mx-auto"
        >
            <div class="flex items-center gap-2">
                <Star class="w-5 h-5 text-yellow-500" />
                <span class="text-2xl font-bold">{stars}</span>
                <span class="text-sm text-muted-foreground">Stars</span>
            </div>
            <div class="flex items-center gap-2">
                <GitFork class="w-5 h-5 text-blue-500" />
                <span class="text-2xl font-bold">{forks}</span>
                <span class="text-sm text-muted-foreground">Forks</span>
            </div>
            <div class="flex items-center gap-2">
                <Users class="w-5 h-5 text-purple-500" />
                <span class="text-2xl font-bold">{contributors.length}</span>
                <span class="text-sm text-muted-foreground">Contributors</span>
            </div>
            {#if latestRelease}
                <div class="flex items-center gap-2">
                    <Tag class="w-5 h-5 text-green-500" />
                    <span class="text-2xl font-bold">{latestRelease}</span>
                    <span class="text-sm text-muted-foreground">Latest</span>
                </div>
            {/if}
        </div>

        <!-- Contributor Avatars -->
        <div class="flex flex-wrap justify-center gap-2 max-w-4xl mx-auto mb-10">
            {#each contributors as contributor (contributor.login)}
                <a
                    href={contributor.html_url}
                    target="_blank"
                    rel="noopener noreferrer"
                    title={contributor.login}
                    class="transition-transform hover:scale-110"
                >
                    <img
                        src="{contributor.avatar_url}&s=80"
                        alt={contributor.login}
                        width="40"
                        height="40"
                        loading="lazy"
                        class="w-10 h-10 rounded-full border-2 border-border/60"
                    />
                </a>
            {/each}
        </div>

        <!-- Discord Button -->
        <div class="text-center">
            <Button
                href="https://discord.gg/vhA4RxKjv7"
                variant="outline"
                size="lg"
                class="gap-2 text-base"
            >
                <svg
                    class="w-5 h-5"
                    viewBox="0 0 24 24"
                    fill="currentColor"
                    aria-hidden="true"
                >
                    <path
                        d="M20.317 4.37a19.791 19.791 0 0 0-4.885-1.515.074.074 0 0 0-.079.037c-.21.375-.444.864-.608 1.25a18.27 18.27 0 0 0-5.487 0 12.64 12.64 0 0 0-.617-1.25.077.077 0 0 0-.079-.037A19.736 19.736 0 0 0 3.677 4.37a.07.07 0 0 0-.032.027C.533 9.046-.32 13.58.099 18.057a.082.082 0 0 0 .031.057 19.9 19.9 0 0 0 5.993 3.03.078.078 0 0 0 .084-.028 14.09 14.09 0 0 0 1.226-1.994.076.076 0 0 0-.041-.106 13.107 13.107 0 0 1-1.872-.892.077.077 0 0 1-.008-.128 10.2 10.2 0 0 0 .372-.292.074.074 0 0 1 .077-.01c3.928 1.793 8.18 1.793 12.062 0a.074.074 0 0 1 .078.01c.12.098.246.198.373.292a.077.077 0 0 1-.006.127 12.299 12.299 0 0 1-1.873.892.077.077 0 0 0-.041.107c.36.698.772 1.362 1.225 1.993a.076.076 0 0 0 .084.028 19.839 19.839 0 0 0 6.002-3.03.077.077 0 0 0 .032-.054c.5-5.177-.838-9.674-3.549-13.66a.061.061 0 0 0-.031-.03z"
                    />
                </svg>
                Join us on Discord
            </Button>
        </div>
    </div>
</section>
