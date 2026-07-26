<script lang="ts">
    // Shared by every page that sends the reader to Settings > Administration. That
    // section is gated on the platform admin flag, which is easy to mistake for the
    // tenant owner role, so the explanation lives here rather than on one page.
    import Callout from "@nocturne/cms/components/Callout.svelte";
</script>

<Callout type="info" title="Can't see the Administration section?">
    Administration is only visible to a <strong>platform admin</strong>. That is a
    separate permission from a tenant's <strong>owner</strong> role, so being the owner
    of your instance is not enough on its own — opening
    <code>/settings/admin</code> without it sends you back to <strong>Settings</strong>.
    On a new install the permission goes to the owner of the first tenant, granted when
    that owner finishes setting up their passkey or sign-in provider.
    <br /><br />
    If you set your instance up on an earlier version, the grant only ran while the API
    was starting, so the owner never received it. Restart the Nocturne API container
    once and sign in again. That works as long as nobody currently holds the
    permission; if someone does, have them grant it to you from
    <strong>Settings &gt; Administration</strong>.
</Callout>
