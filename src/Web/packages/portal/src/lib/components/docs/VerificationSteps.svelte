<script lang="ts">
    import CodeBlock from "./CodeBlock.svelte";
    import SiteLink from "./install/SiteLink.svelte";
    import { getInstallGuide } from "./install/guide.svelte.js";

    interface Props {
        /** `docker compose` with the `-f` arguments of the reader's setup. */
        compose?: string;
        /** The reader's own reverse proxy serves HTTPS, not the bundled Caddy. */
        ownProxy?: boolean;
    }

    let { compose = "docker compose", ownProxy = false }: Props = $props();

    const guide = getInstallGuide();

    // Commands, not user-facing text.
    // @wc-ignore
    const psCommand = $derived(`${compose} ps`);
    // @wc-ignore
    const logsCommand = $derived(`${compose} logs -f`);
    // @wc-ignore
    const curlCommand = $derived(`curl https://${guide?.host ?? "your-domain"}/api/v1/status.json`);
</script>

<div class="space-y-4 mb-8">
    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Check that all containers are running:</strong>
        </p>
        <CodeBlock code={psCommand} />
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">View the logs for any errors:</strong>
        </p>
        <CodeBlock code={logsCommand} />
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Test the API is responding:</strong>
        </p>
        <CodeBlock code={curlCommand} />
        <p class="text-xs text-muted-foreground mt-1">
            {#if !guide?.host}
                Replace <code class="bg-muted/50 px-1 py-0.5 rounded">your-domain</code> with your
                <code class="bg-muted/50 px-1 py-0.5 rounded">BASE_DOMAIN</code>.
            {/if}
            The server returns a JSON response with its status.
            {#if ownProxy}
                If your proxy does not forward to Nocturne yet, query the gateway on host port
                8080 instead.
            {/if}
        </p>
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Open your site:</strong>
        </p>
        <p class="text-muted-foreground"><SiteLink /></p>
    </div>
</div>
