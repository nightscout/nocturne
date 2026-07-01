<script lang="ts">
    // The scope list and descriptions are the single source of truth shared with the
    // OAuth consent screen — derived from the backend OAuthScope enum (NSwag-generated),
    // so this table can never drift from the scopes the API actually accepts. Adding a
    // backend scope regenerates the enum; OAUTH_SCOPE_DESCRIPTIONS is typed
    // Record<OAuthScope, string>, so the build fails until the new scope gets a description.
    import {
        OAUTH_AVAILABLE_SCOPES,
        OAUTH_SCOPE_DESCRIPTIONS,
    } from "@nocturne/app/constants/oauth-scopes";
</script>

<table>
    <thead>
        <tr>
            <th>Scope</th>
            <th>Access</th>
        </tr>
    </thead>
    <tbody>
        {#each OAUTH_AVAILABLE_SCOPES as scope (scope)}
            <tr>
                <td><code>{scope}</code></td>
                <td>{OAUTH_SCOPE_DESCRIPTIONS[scope]}</td>
            </tr>
        {/each}
    </tbody>
</table>
