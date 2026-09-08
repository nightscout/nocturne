<script lang="ts">
    import "../app.css";
    import SiteHeader from "$lib/components/SiteHeader.svelte";
    import SiteFooter from "$lib/components/SiteFooter.svelte";
    import { afterNavigate } from "$app/navigation";
    import { PLAUSIBLE_DOMAIN } from "$lib/config";
    import {
        PLAUSIBLE_ENABLED,
        PLAUSIBLE_EVENT_API,
        PLAUSIBLE_SCRIPT_SRC,
        trackPageview,
    } from "$lib/analytics";

    let { children } = $props();

    // Fires after the initial load as well, so the manual tracker needs no mount hook.
    afterNavigate(({ to }) => {
        if (to) trackPageview(to.url);
    });
</script>

<svelte:head>
    {#if PLAUSIBLE_ENABLED}
        <!-- Queues calls made before the deferred script runs; it drains the queue on load. -->
        <script>
            window.plausible =
                window.plausible ||
                function () {
                    (window.plausible.q = window.plausible.q || []).push(arguments);
                };
        </script>
        <script
            defer
            data-domain={PLAUSIBLE_DOMAIN}
            data-api={PLAUSIBLE_EVENT_API}
            src={PLAUSIBLE_SCRIPT_SRC}
        ></script>
    {/if}
</svelte:head>

<div
    class="min-h-screen flex flex-col bg-linear-to-br from-background via-background to-primary/5"
>
    <SiteHeader />

    <main class="flex-1">
        {@render children()}
    </main>

    <SiteFooter />
</div>
