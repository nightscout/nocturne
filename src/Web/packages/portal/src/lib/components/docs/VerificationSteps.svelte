<script lang="ts">
    import CodeBlock from "./CodeBlock.svelte";
</script>

<div class="space-y-4 mb-8">
    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Check that all containers are running:</strong>
        </p>
        <CodeBlock code="docker compose ps" />
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">View the logs for any errors:</strong>
        </p>
        <CodeBlock code="docker compose logs -f" />
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Test the API is responding:</strong>
        </p>
        <CodeBlock code="curl https://your-domain/api/v1/status.json" />
        <p class="text-xs text-muted-foreground mt-1">
            Replace <code class="bg-muted/50 px-1 py-0.5 rounded">your-domain</code> with your
            <code class="bg-muted/50 px-1 py-0.5 rounded">BASE_DOMAIN</code>. The bundled Caddy proxy
            serves HTTPS on ports 80 and 443; you should receive a JSON response with server status.
            (With a bring-your-own reverse proxy, query the gateway on host port 8080 instead.)
        </p>
    </div>
</div>
