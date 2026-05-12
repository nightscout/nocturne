<div class="space-y-4 mb-8">
    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Check that all containers are running:</strong>
        </p>
        <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto"><code>docker compose ps</code></pre>
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">View the logs for any errors:</strong>
        </p>
        <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto"><code>docker compose logs -f</code></pre>
    </div>

    <div>
        <p class="text-muted-foreground mb-2">
            <strong class="text-foreground">Test the API is responding:</strong>
        </p>
        <pre class="p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto"><code>curl http://localhost:8080/api/v1/status.json</code></pre>
        <p class="text-xs text-muted-foreground mt-1">
            The YARP gateway listens on port 8080. You should receive a JSON response with server status.
        </p>
    </div>
</div>
