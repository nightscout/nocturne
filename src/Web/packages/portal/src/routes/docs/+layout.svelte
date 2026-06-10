<script lang="ts">
    import DocsSidebar from "$lib/components/DocsSidebar.svelte";
    import { Button } from "@nocturne/ui/ui/button";
    import { Menu, X } from "@lucide/svelte";

    let { children } = $props();
    let sidebarOpen = $state(false);
</script>

<div class="container mx-auto px-4 py-8">
    <div class="flex gap-8">
        <!-- Mobile sidebar toggle -->
        <div class="lg:hidden fixed bottom-4 right-4 z-50">
            <Button
                variant="default"
                size="icon"
                class="rounded-full shadow-lg"
                onclick={() => (sidebarOpen = !sidebarOpen)}
            >
                {#if sidebarOpen}
                    <X class="w-5 h-5" />
                {:else}
                    <Menu class="w-5 h-5" />
                {/if}
            </Button>
        </div>

        <!-- Mobile sidebar overlay -->
        {#if sidebarOpen}
            <div
                class="fixed inset-0 bg-background/80 backdrop-blur-sm z-40 lg:hidden"
                onclick={() => (sidebarOpen = false)}
                onkeydown={(e) => e.key === "Escape" && (sidebarOpen = false)}
                role="button"
                tabindex="0"
            ></div>
        {/if}

        <!-- Sidebar -->
        <aside
            class="fixed lg:sticky top-0 left-0 z-50 lg:z-0 h-screen lg:h-auto w-64 shrink-0
                   bg-background lg:bg-transparent border-r lg:border-0 border-border/40
                   overflow-y-auto lg:overflow-visible
                   transform transition-transform duration-200 ease-in-out
                   {sidebarOpen ? 'translate-x-0' : '-translate-x-full'} lg:translate-x-0
                   pt-16 lg:pt-0 px-4 lg:px-0 pb-8 lg:pb-0"
        >
            <!-- Cap the sticky sidebar to the viewport and let it scroll on its own,
                 so a nav taller than the screen no longer requires scrolling the
                 article to the bottom before its lower entries become reachable. -->
            <div class="lg:sticky lg:top-24 lg:max-h-[calc(100vh-7rem)] lg:overflow-y-auto">
                <DocsSidebar />
            </div>
        </aside>

        <!-- Main content -->
        <main class="flex-1 min-w-0">
            <article class="prose prose-neutral dark:prose-invert max-w-none">
                {@render children()}
            </article>
        </main>
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
</style>
