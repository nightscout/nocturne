<script lang="ts">
    import { Key } from "@nocturne/ui/keys";
    import DocsSidebar from "#lib/components/DocsSidebar.svelte";
    import DocsScrollDepth from "#lib/components/docs/DocsScrollDepth.svelte";
    import DocsReadTime from "#lib/components/docs/DocsReadTime.svelte";
    import DocsInstallSteps from "#lib/components/docs/DocsInstallSteps.svelte";
    import DocsFeedback from "#lib/components/docs/DocsFeedback.svelte";
    import { page } from "$app/state";
    import { Button } from "@nocturne/ui/ui/button";
    import Menu from "@lucide/svelte/icons/menu";
    import X from "@lucide/svelte/icons/x";

    let { children } = $props();
    let sidebarOpen = $state(false);
</script>

<DocsScrollDepth />
<DocsReadTime />
<DocsInstallSteps />

<div class="container mx-auto px-4 py-8">
    <div class="flex gap-8">
        <div class="lg:hidden fixed bottom-4 right-4 z-50">
            <!-- eslint-disable-next-line shadcn/no-restyle -- the site's only floating action button, pinned over the page on narrow screens -->
            <Button class="rounded-full shadow-lg"
                variant="default"
                size="icon"
                onclick={() => (sidebarOpen = !sidebarOpen)}
            >
                {#if sidebarOpen}
                    <X class="w-5 h-5" />
                {:else}
                    <Menu class="w-5 h-5" />
                {/if}
            </Button>
        </div>

        {#if sidebarOpen}
            <div
                class="fixed inset-0 bg-background/80 backdrop-blur-sm z-40 lg:hidden"
                onclick={() => (sidebarOpen = false)}
                onkeydown={(e) => e.key === Key.Escape && (sidebarOpen = false)}
                role="button"
                tabindex="0"
            ></div>
        {/if}

        <aside
            class="fixed lg:sticky top-0 left-0 z-50 lg:z-0 h-screen lg:h-auto w-64 shrink-0
                   bg-background lg:bg-transparent border-r lg:border-0 border-border/40
                   overflow-y-auto lg:overflow-visible
                   transform transition-transform duration-200 ease-in-out
                   {sidebarOpen ? 'translate-x-0' : '-translate-x-full'} lg:translate-x-0
                   pt-16 lg:pt-0 px-4 lg:px-0 pb-8 lg:pb-0"
        >
            <!-- Cap the sticky sidebar to the viewport and let the nav scroll on its own,
                 so a nav taller than the screen no longer requires scrolling the
                 article to the bottom before its lower entries become reachable. The
                 feedback prompt stays pinned under it. -->
            <div class="lg:sticky lg:top-24 lg:max-h-[calc(100vh-7rem)] lg:flex lg:flex-col">
                <div class="lg:min-h-0 lg:overflow-y-auto">
                    <DocsSidebar />
                </div>
                {#key page.url.pathname}
                    <DocsFeedback class="hidden lg:block shrink-0 mt-6 border-t border-border/40 pt-4" />
                {/key}
            </div>
        </aside>

        <div class="flex-1 min-w-0 docs-main">
            <article class="prose prose-neutral dark:prose-invert max-w-none">
                {@render children()}
            </article>
            <!-- Keyed so each page asks afresh; the layout survives navigation between pages.
                 Below lg the sidebar is a drawer, so the prompt sits under the article. -->
            {#key page.url.pathname}
                <DocsFeedback class="lg:hidden mt-12 border-t border-border/40 pt-6" />
            {/key}
        </div>
    </div>
</div>

<style>
    /* Markdown tables can hold long, unbreakable strings (callback URLs, paths).
       Let them scroll horizontally on narrow screens instead of forcing the
       whole page to overflow. */
    article.prose :global(table) {
        display: block;
        max-width: 100%;
        overflow-x: auto;
    }

    /* The article caps its measure at max-w-3xl, so on a wide screen the rest
       of the main column is empty. A hero reclaims it: above the float
       breakpoint the page block widens to take the gutter, while the prose
       inside it stays capped, so only the floated paint uses the extra width
       and the reading measure never changes. */
    /* A non-wrapping hero is placed against this box's right edge. */
    .docs-main :global(.max-w-3xl) {
        position: relative;
    }

    @media (min-width: 1024px) {
        .docs-main :global(.max-w-3xl) {
            max-width: min(
                68rem,
                min(1536px, 100vw - 2rem) - 16rem - 2rem - 2rem
            );
        }

        .docs-main :global(.max-w-3xl) > :global(*) {
            max-width: 48rem;
        }

        /* The floated hero and anything sharing its lines are the exception:
           those lines are already shortened by the paint. */
        .docs-main :global(.nwc-hero) {
            max-width: none;
        }
    }

    /* Typography wraps inline code in literal backticks; the code tint already marks it. */
    article.prose :global(code::before),
    article.prose :global(code::after) {
        content: none;
    }
</style>
