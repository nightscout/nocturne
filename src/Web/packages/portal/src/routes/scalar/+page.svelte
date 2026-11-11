<script lang="ts">
    import { onMount } from "svelte";
    import ExternalLink from "@lucide/svelte/icons/external-link";
    import { DEMO_ENABLED, DEMO_WEB_URL, SCALAR_API_URL } from "#lib/config.js";
    import { createDemoTokenSource, type ScalarSecurityEntry } from "#lib/scalar/demo-token.js";

    // With a demo, the specs come from the demo's host, so their server is the demo and
    // "Send" goes there with a demo token (the API admits this origin; see DocsSiteCorsPolicy).
    // Without one there is no tenant to send to and nothing to send with, so the reference
    // is for reading only.
    const demoUrl = DEMO_ENABLED && DEMO_WEB_URL ? DEMO_WEB_URL : null;
    const specsUrl = demoUrl ?? SCALAR_API_URL;

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
    let sendsToDemo = $state(false);

    async function demoAuthentication(): Promise<Record<string, unknown> | null> {
        if (!demoUrl) return null;

        const tokens = createDemoTokenSource(demoUrl);
        let token: string;
        try {
            token = await tokens.token();
        } catch {
            // The demo is down or does not admit this origin: fall back to reading only.
            return null;
        }

        return {
            authentication: {
                // One scheme per document: bearer is the Nocturne document's, apiSecret the
                // Nightscout document's. Listing two of one document selects both at once.
                preferredSecurityScheme: ["bearer", "apiSecret"],
                securitySchemes: { bearer: { token } },
            },
            // Scalar resolves credentials into requestBuilder.security; the request it passes
            // alongside is a copy, so changes to it are not sent.
            onBeforeRequest: ({ requestBuilder }: { requestBuilder: { security: ScalarSecurityEntry[] } }) =>
                tokens.refresh(requestBuilder.security),
        };
    }

    onMount(() => {
        let instance: { destroy?: () => void } | undefined;
        let disposed = false;

        const sources = [
            {
                title: "Nocturne API",
                slug: "nocturne",
                url: `${specsUrl}/openapi/nocturne.json`,
                default: true,
            },
            {
                title: "Nightscout API",
                slug: "nightscout",
                url: `${specsUrl}/openapi/nightscout.json`,
            },
        ];

        const scriptLoaded = new Promise<void>((resolve) => {
            const script = document.createElement("script");
            script.src = SCALAR_SCRIPT;
            script.onload = () => resolve();
            document.head.appendChild(script);
        });

        Promise.all([demoAuthentication(), scriptLoaded]).then(([demo]) => {
            const scalar: unknown = Reflect.get(window, "Scalar");
            if (disposed || !isScalarGlobal(scalar)) return;

            sendsToDemo = demo !== null;
            instance = scalar.createApiReference(container, {
                theme: "mars",
                sources,
                ...(demo ?? { hideTestRequestButton: true, hideClientButton: true }),
            });
        });

        // Diagrams embedded in the OpenAPI descriptions are rendered by the API's
        // own mermaid lazy-loader, served alongside the specs. Reusing it (rather
        // than forking a copy into the portal) keeps rendering in sync with the API.
        // It observes the document, so load order relative to Scalar doesn't matter.
        const mermaidCss = document.createElement("link");
        mermaidCss.rel = "stylesheet";
        mermaidCss.href = `${specsUrl}/scalar/mermaid-loader.css`;
        document.head.appendChild(mermaidCss);

        const mermaid = document.createElement("script");
        mermaid.type = "module";
        mermaid.crossOrigin = "anonymous";
        mermaid.src = `${specsUrl}/scalar/mermaid-loader.js`;
        document.head.appendChild(mermaid);

        return () => {
            disposed = true;
            instance?.destroy?.();
            document.head.querySelector(`script[src="${SCALAR_SCRIPT}"]`)?.remove();
            mermaidCss.remove();
            mermaid.remove();
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
        class="px-4 py-2 border-b border-border/60 bg-card/50 flex flex-wrap items-center justify-between gap-2 text-sm"
    >
        <span class="text-muted-foreground">
            {#if sendsToDemo}
                Requests go to the demo instance. It has sample data and resets on a schedule.
            {:else}
                To send requests, open this reference on the demo instance. The demo has sample
                data and resets on a schedule.
            {/if}
        </span>
        <a
            href={sendsToDemo ? demoUrl : `${demoUrl}/scalar`}
            target="_blank"
            rel="external noopener noreferrer"
            class="inline-flex items-center gap-1.5 font-medium underline underline-offset-4"
        >
            {sendsToDemo ? "Open the demo" : "Open the reference on the demo"}
            <ExternalLink class="size-3.5" />
        </a>
    </div>
{/if}
<div bind:this={container} class={demoUrl ? "h-[calc(100vh-7rem)]" : "h-[calc(100vh-4rem)]"}></div>
