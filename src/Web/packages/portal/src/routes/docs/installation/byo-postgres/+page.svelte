<script lang="ts">
    import { Database } from "@lucide/svelte";
</script>

<div class="max-w-3xl">
    <div class="flex items-center gap-3 mb-4">
        <div class="w-10 h-10 rounded-lg bg-emerald-500/15 flex items-center justify-center">
            <Database class="w-5 h-5 text-emerald-500" />
        </div>
        <h1 class="text-4xl font-bold tracking-tight">Bring Your Own PostgreSQL</h1>
    </div>

    <p class="text-lg text-muted-foreground mb-8">
        For deployments that use a managed PostgreSQL service or an existing shared database server
        instead of Nocturne's bundled Postgres container.
    </p>

    <div class="p-4 rounded-lg border border-amber-500/30 bg-amber-500/5 text-sm text-foreground mb-8">
        <p class="font-medium mb-1">Three non-privileged roles are required</p>
        <p class="text-muted-foreground">
            Nocturne enforces Row Level Security on every medical-data table. RLS is only meaningful
            when the runtime connection cannot bypass it, so Nocturne requires three separate roles:
        </p>
        <ul class="list-disc pl-5 mt-2 space-y-1 text-muted-foreground">
            <li>
                <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_migrator</code> —
                owns the schema and runs migrations.
            </li>
            <li>
                <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_app</code> —
                runtime connection for the .NET API. Cannot bypass RLS and has no DDL privileges.
            </li>
            <li>
                <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_web</code> —
                used by the SvelteKit web app's bot framework to store chat-platform state. Owns
                only its own <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">chat_state_*</code>
                tables (not tenant-scoped, no PHI).
            </li>
        </ul>
    </div>

    <h2 class="text-2xl font-bold mt-8 mb-4">Setup steps</h2>

    <ol class="list-decimal pl-6 space-y-4 text-muted-foreground">
        <li>
            Download
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">bootstrap-roles.sql</code>
            from the Nocturne repository under
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">docs/postgres/</code>.
        </li>
        <li>
            Edit the file and replace the three
            <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">REPLACE_ME</code>
            passwords with strong values from your secrets manager.
        </li>
        <li>
            Run as a PostgreSQL superuser against your Nocturne database:
            <pre class="mt-2 p-3 rounded-lg bg-muted/50 border border-border/60 text-sm overflow-x-auto"><code>psql -U &lt;superuser&gt; -d &lt;nocturne-database&gt; -f bootstrap-roles.sql</code></pre>
        </li>
        <li>
            Set three connection strings in Nocturne's environment:
            <ul class="list-disc pl-5 mt-2 space-y-1">
                <li>
                    <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">ConnectionStrings__nocturne-postgres</code>
                    — use the <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_app</code> role.
                </li>
                <li>
                    <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">ConnectionStrings__nocturne-postgres-migrator</code>
                    — use the <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_migrator</code> role.
                </li>
                <li>
                    <code class="text-xs bg-muted/50 px-1.5 py-0.5 rounded font-mono">NOCTURNE_POSTGRES_URI</code>
                    — a <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">postgresql://</code>
                    URL for the <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">nocturne_web</code>
                    role (consumed by the SvelteKit bot state adapter).
                </li>
            </ul>
        </li>
        <li>
            Start Nocturne. If either role is missing or misconfigured, the startup error will
            inline the exact SQL needed to fix it.
        </li>
    </ol>

    <h2 class="text-2xl font-bold mt-10 mb-4">Notes</h2>
    <ul class="list-disc pl-6 space-y-2 text-muted-foreground">
        <li>
            The two roles only need to be created once per database. Re-running
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">bootstrap-roles.sql</code> is
            idempotent.
        </li>
        <li>
            Neither role has <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">SUPERUSER</code>
            or <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">BYPASSRLS</code>. Nocturne
            refuses to start if the runtime role can bypass Row Level Security.
        </li>
        <li>
            On managed PostgreSQL services (RDS, Cloud SQL, Supabase, Neon), run the script as the
            database owner your provider gave you rather than the literal
            <code class="text-xs bg-muted/50 px-1 py-0.5 rounded">postgres</code> user.
        </li>
    </ul>
</div>
