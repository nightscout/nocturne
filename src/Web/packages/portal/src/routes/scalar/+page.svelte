<script lang="ts">
    import { onMount } from "svelte";
    import { ExternalLink } from "@lucide/svelte";
    import { DEMO_ENABLED, DEMO_WEB_URL, SCALAR_API_URL } from "$lib/config";

    // This embed is for reading. Sending a request needs a tenant to send it to and a
    // credential to send with it, and this page is on a different domain to any Nocturne
    // instance — so point people at the demo's own copy of the reference, which is
    // same-origin with the demo API and arrives already authorized.
    const demoScalarUrl = DEMO_ENABLED && DEMO_WEB_URL ? `${DEMO_WEB_URL}/scalar` : null;

    // Pinned standalone build. Scalar is a Vue app, so it's loaded at runtime in
    // the browser rather than bundled — keeps it out of the Svelte SSR/prerender
    // graph (which can't resolve Vue's deps) and off the portal's dependency tree.
    const SCALAR_SCRIPT = "https://cdn.jsdelivr.net/npm/@scalar/api-reference@1.57.5";

    type ScalarGlobal = {
        createApiReference: (
            el: HTMLElement,
            config: Record<string, unknown>,
        ) => { destroy?: () => void };
    };

    let container: HTMLDivElement;

    onMount(() => {
        let instance: { destroy?: () => void } | undefined;

        const config = {
            theme: "mars",
            sources: [
                {
                    title: "Nocturne API",
                    slug: "nocturne",
                    url: `${SCALAR_API_URL}/openapi/nocturne.json`,
                    default: true,
                },
                {
                    title: "Nightscout API",
                    slug: "nightscout",
                    url: `${SCALAR_API_URL}/openapi/nightscout.json`,
                },
            ],
        };

        const script = document.createElement("script");
        script.src = SCALAR_SCRIPT;
        script.onload = () => {
            const scalar = (window as unknown as { Scalar?: ScalarGlobal }).Scalar;
            instance = scalar?.createApiReference(container, config);
        };
        document.head.appendChild(script);

        // Diagrams embedded in the OpenAPI descriptions are rendered by the API's
        // own mermaid lazy-loader, served alongside the specs. Reusing it (rather
        // than forking a copy into the portal) keeps rendering in sync with the API.
        // It observes the document, so load order relative to Scalar doesn't matter.
        const mermaidCss = document.createElement("link");
        mermaidCss.rel = "stylesheet";
        mermaidCss.href = `${SCALAR_API_URL}/scalar/mermaid-loader.css`;
        document.head.appendChild(mermaidCss);

        const mermaid = document.createElement("script");
        mermaid.type = "module";
        mermaid.crossOrigin = "anonymous";
        mermaid.src = `${SCALAR_API_URL}/scalar/mermaid-loader.js`;
        document.head.appendChild(mermaid);

        return () => {
            instance?.destroy?.();
            script.remove();
            mermaidCss.remove();
            mermaid.remove();
        };
    });
</script>

<svelte:head>
    <title>API Reference - Nocturne</title>
    <meta
        name="description"
        content="Interactive Nocturne API documentation powered by Scalar — explore endpoints, test requests, and integrate with Nocturne."
    />
</svelte:head>

{#if demoScalarUrl}
    <div
        class="px-4 py-2 border-b border-border/60 bg-card/50 flex flex-wrap items-center justify-between gap-2 text-sm"
    >
        <span class="text-muted-foreground">
            Want to send real requests? Open this reference on the demo instance — it comes
            already signed in, and the demo resets on a schedule.
        </span>
        <a
            href={demoScalarUrl}
            target="_blank"
            rel="noopener noreferrer"
            class="inline-flex items-center gap-1.5 font-medium underline underline-offset-4"
        >
            Try it on the demo
            <ExternalLink class="size-3.5" />
        </a>
    </div>
    <div bind:this={container} class="h-[calc(100vh-7rem)]"></div>
{:else}
    <div bind:this={container} class="h-[calc(100vh-4rem)]"></div>
{/if}
