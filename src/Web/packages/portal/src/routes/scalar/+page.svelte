<script lang="ts" module>
    import { DEMO_ENABLED, DEMO_WEB_URL } from "#lib/config.js";
    import { createDemoTokenSource } from "#lib/scalar/demo-token.js";

    const demoUrl = DEMO_ENABLED && DEMO_WEB_URL ? DEMO_WEB_URL : null;

    // One source per page load, so leaving and returning to this route opens no new demo session.
    const demoTokens = demoUrl ? createDemoTokenSource(demoUrl) : null;
</script>

<script lang="ts">
    import { onMount } from "svelte";
    import ExternalLink from "@lucide/svelte/icons/external-link";
    import { SCALAR_API_URL } from "#lib/config.js";
    import { DEMO_TOKEN_PLACEHOLDER, isAbort, type ScalarSecurityEntry } from "#lib/scalar/demo-token.js";

    // Pinned standalone build. Scalar is a Vue app, so it's loaded at runtime in
    // the browser rather than bundled, which keeps it out of the Svelte SSR/prerender
    // graph (which can't resolve Vue's deps) and off the portal's dependency tree.
    const SCALAR_SCRIPT = "https://cdn.jsdelivr.net/npm/@scalar/api-reference@1.57.5";

    type ScalarGlobal = {
        createApiReference: (
            el: HTMLElement,
            config: Record<string, unknown>,
        ) => { destroy?: () => void };
    };

    function isScalarGlobal(value: unknown): value is ScalarGlobal {
        return (
            typeof value === "object" &&
            value !== null &&
            "createApiReference" in value &&
            typeof value.createApiReference === "function"
        );
    }

    let container: HTMLDivElement;

    // With the demo reachable, the specs come from its host, so their server is the demo and
    // "Send" goes there with a demo token (the API admits this origin on the demo only; see
    // DocsSiteCorsPolicy). Otherwise there is no tenant to send to, so the reference is for reading.
    let mode = $state<"pending" | "demo" | "read-only">("pending");

    function scalarConfig(sendsToDemo: boolean): Record<string, unknown> {
        const specsUrl = sendsToDemo && demoUrl ? demoUrl : SCALAR_API_URL;
        const sources = [
            { title: "Nocturne API", slug: "nocturne", url: `${specsUrl}/openapi/nocturne.json`, default: true },
            { title: "Nightscout API", slug: "nightscout", url: `${specsUrl}/openapi/nightscout.json` },
        ];

        if (!sendsToDemo || !demoTokens) {
            return { theme: "mars", sources, hideTestRequestButton: true, hideClientButton: true };
        }

        return {
            theme: "mars",
            sources,
            authentication: {
                // One scheme per document: bearer is the Nocturne document's, apiSecret the
                // Nightscout document's. Listing two of one document selects both at once.
                preferredSecurityScheme: ["bearer", "apiSecret"],
                securitySchemes: { bearer: { token: DEMO_TOKEN_PLACEHOLDER } },
            },
            // Scalar resolves credentials into requestBuilder.security; the request it passes
            // alongside is a copy, so changes to it are not sent.
            onBeforeRequest: ({ requestBuilder }: { requestBuilder: { security: ScalarSecurityEntry[] } }) =>
                demoTokens.refresh(requestBuilder.security),
        };
    }

    function loadScript(src: string): Promise<void> {
        return new Promise((resolve) => {
            const script = document.createElement("script");
            script.src = src;
            script.onload = () => resolve();
            document.head.appendChild(script);
        });
    }

    function addMermaid(baseUrl: string): () => void {
        // Diagrams embedded in the OpenAPI descriptions are rendered by the API's own mermaid
        // lazy-loader, served alongside the specs, so rendering stays in sync with the API.
        // It observes the document, so load order relative to Scalar doesn't matter.
        const css = document.createElement("link");
        css.rel = "stylesheet";
        css.href = `${baseUrl}/scalar/mermaid-loader.css`;
        document.head.appendChild(css);

        const script = document.createElement("script");
        script.type = "module";
        script.crossOrigin = "anonymous";
        script.src = `${baseUrl}/scalar/mermaid-loader.js`;
        document.head.appendChild(script);

        return () => {
            css.remove();
            script.remove();
        };
    }

    onMount(() => {
        let instance: { destroy?: () => void } | undefined;
        let removeMermaid: (() => void) | undefined;
        const abort = new AbortController();

        const reachable = demoTokens ? demoTokens.reachable(abort.signal) : Promise.resolve(false);

        Promise.all([reachable, loadScript(SCALAR_SCRIPT)])
            .then(([sendsToDemo]) => {
                const scalar: unknown = Reflect.get(window, "Scalar");
                if (abort.signal.aborted || !isScalarGlobal(scalar)) return;

                mode = sendsToDemo ? "demo" : "read-only";
                removeMermaid = addMermaid(sendsToDemo && demoUrl ? demoUrl : SCALAR_API_URL);
                instance = scalar.createApiReference(container, scalarConfig(sendsToDemo));
            })
            .catch((error: unknown) => {
                if (!isAbort(error)) throw error;
            });

        return () => {
            abort.abort();
            instance?.destroy?.();
            removeMermaid?.();
            document.head.querySelector(`script[src="${SCALAR_SCRIPT}"]`)?.remove();
        };
    });
</script>

<svelte:head>
    <title>API Reference - Nocturne</title>
    <meta
        name="description"
        content="Interactive Nocturne API documentation powered by Scalar. Explore endpoints, test requests, and integrate with Nocturne."
    />
</svelte:head>

{#if demoUrl}
    <div
        class="min-h-10 px-4 py-2 border-b border-border/60 bg-card/50 flex flex-wrap items-center justify-between gap-2 text-sm"
    >
        {#if mode !== "pending"}
            <span class="text-muted-foreground">
                {#if mode === "demo"}
                    Requests go to the demo instance. It has sample data and resets on a schedule.
                {:else}
                    To send requests, open this reference on the demo instance. The demo has sample
                    data and resets on a schedule.
                {/if}
            </span>
            <a
                href={mode === "demo" ? demoUrl : `${demoUrl}/scalar`}
                target="_blank"
                rel="external noopener noreferrer"
                class="inline-flex items-center gap-1.5 font-medium underline underline-offset-4"
            >
                {mode === "demo" ? "Open the demo" : "Open the reference on the demo"}
                <ExternalLink class="size-3.5" />
            </a>
        {/if}
    </div>
{/if}
<div bind:this={container} class={demoUrl ? "h-[calc(100vh-7rem)]" : "h-[calc(100vh-4rem)]"}></div>
