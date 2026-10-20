<script lang="ts">
    // The scope list and labels are shared with the OAuth consent screen and derived from the
    // backend OAuthScope enum, so this table can never drift from the scopes the API accepts.
    import {
        OAUTH_AVAILABLE_SCOPES,
        getOAuthScopeDescription,
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
                <td>{getOAuthScopeDescription(scope)}</td>
            </tr>
        {/each}
    </tbody>
</table>
