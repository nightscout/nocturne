<script lang="ts">
    import { onMount } from "svelte";
    import { SCALAR_API_URL } from "$lib/config";

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

        return () => {
            instance?.destroy?.();
            script.remove();
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

<div bind:this={container} class="h-[calc(100vh-4rem)]"></div>
