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
        <CodeBlock code="curl http://localhost:8080/api/v1/status.json" />
        <p class="text-xs text-muted-foreground mt-1">
            The YARP gateway listens on port 8080. You should receive a JSON response with server status.
        </p>
    </div>
</div>
