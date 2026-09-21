<script lang="ts">
  // Glooko XT signs a patient in with a one-time code it emails them, so the sign-in cannot run
  // in the background: the user enters email and password here, Glooko XT sends the code, the
  // user types it back, and the server trades it for a year-long token stored as the connector
  // secret. The password is used for that one request and never stored.
  import {
    requestCode as requestGlookoXtCode,
    complete as completeGlookoXtConnect,
  } from "$lib/api/generated/glookoXtConnects.generated.remote";
  import { describeSubmitError } from "$lib/forms/submit-error";
  import { Button } from "$lib/components/ui/button";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { CheckCircle2, MailCheck } from "lucide-svelte";

  let {
    email = $bindable(""),
    onConnected,
  }: {
    // The connector's own email field, rendered by the surrounding credentials card; the sign-in
    // reads it from there rather than asking a second time.
    email?: string;
    // Called once the token is stored, with the account that signed in.
    onConnected?: (info: { email: string; server?: string | null; tokenExpiresAt?: Date | string | null }) => void;
  } = $props();

  type Phase = "idle" | "awaiting-code" | "done";

  let phase = $state<Phase>("idle");
  let password = $state("");
  let code = $state("");
  let busy = $state(false);
  let error = $state<string | null>(null);
  let tokenExpiresAt = $state<Date | string | null>(null);

  const canRequest = $derived(email.trim().length > 0 && password.length > 0 && !busy);
  const emailMissing = $derived(email.trim().length === 0);
  const canComplete = $derived(code.trim().length > 0 && !busy);

  async function sendCode() {
    if (!canRequest) return;
    busy = true;
    error = null;
    try {
      const res = await requestGlookoXtCode({ email: email.trim(), password });
      if (!res.success) {
        error = "Glooko XT did not accept the sign-in. Check the email and password.";
        return;
      }
      // The password has done its one job; drop it from memory before the code step.
      password = "";
      phase = "awaiting-code";
    } catch (e) {
      error = describeSubmitError(e, "Could not start the Glooko XT sign-in.");
    } finally {
      busy = false;
    }
  }

  async function finishConnect() {
    if (!canComplete) return;
    busy = true;
    error = null;
    try {
      const res = await completeGlookoXtConnect({ email: email.trim(), code: code.trim() });
      if (!res.success) {
        error = "Glooko XT did not accept that code. Request a new one and try again.";
        return;
      }
      tokenExpiresAt = res.tokenExpiresAt ?? null;
      phase = "done";
      onConnected?.({ email: res.email ?? email.trim(), server: res.server ?? "XT", tokenExpiresAt });
    } catch (e) {
      error = describeSubmitError(e, "Could not complete the Glooko XT sign-in.");
    } finally {
      busy = false;
    }
  }

  function reset() {
    phase = "idle";
    code = "";
    password = "";
    error = null;
  }

  function formatExpiry(value: Date | string): string {
    const d = value instanceof Date ? value : new Date(value);
    return Number.isNaN(d.getTime()) ? String(value) : d.toLocaleDateString();
  }
</script>

<div class="space-y-4" data-testid="glookoxt-connect">
  <p class="text-sm text-muted-foreground">
    Glooko XT signs you in with a code sent to your email. Your password is only used to request
    that code and is never stored: Nocturne keeps a revocable access token instead.
  </p>

  {#if error}
    <p class="text-sm text-destructive">{error}</p>
  {/if}

  {#if phase === "idle"}
    <form
      class="space-y-4"
      onsubmit={(e) => {
        e.preventDefault();
        void sendCode();
      }}
    >
      <div class="space-y-2">
        <Label for="glookoxt-password">Password</Label>
        <Input
          id="glookoxt-password"
          type="password"
          autocomplete="current-password"
          bind:value={password}
          disabled={busy}
        />
        <p class="text-sm text-muted-foreground">
          {#if emailMissing}
            Fill in the email above first.
          {:else}
            Used once to request the code for <span class="font-medium text-foreground">{email}</span>.
          {/if}
        </p>
      </div>
      <Button type="submit" variant="outline" disabled={!canRequest}>
        <MailCheck class="h-4 w-4 mr-2" />
        {busy ? "Sending code…" : "Send me a code"}
      </Button>
    </form>
  {/if}

  {#if phase === "awaiting-code"}
    <form
      class="space-y-4"
      onsubmit={(e) => {
        e.preventDefault();
        void finishConnect();
      }}
    >
      <div class="space-y-2">
        <Label for="glookoxt-code">Code from the email</Label>
        <Input
          id="glookoxt-code"
          inputmode="numeric"
          autocomplete="one-time-code"
          bind:value={code}
          disabled={busy}
          class="font-mono tracking-widest"
          placeholder="1234"
        />
        <p class="text-sm text-muted-foreground">
          Glooko XT has emailed a code to <span class="font-medium text-foreground">{email}</span>.
          Codes expire after a few minutes.
        </p>
      </div>
      <div class="flex gap-2">
        <Button type="submit" disabled={!canComplete}>
          {busy ? "Finishing…" : "Finish connecting"}
        </Button>
        <Button type="button" variant="ghost" onclick={reset} disabled={busy}>Start over</Button>
      </div>
    </form>
  {/if}

  {#if phase === "done"}
    <div class="flex items-start gap-2 text-sm">
      <CheckCircle2 class="h-5 w-5 text-green-600 shrink-0" />
      <div>
        <p class="font-medium">Glooko XT connected.</p>
        <p class="text-muted-foreground">
          Signed in as {email}. Syncing will use the stored token automatically{tokenExpiresAt
            ? ` until ${formatExpiry(tokenExpiresAt)}`
            : ""}.
        </p>
      </div>
    </div>
    <Button variant="outline" size="sm" onclick={reset}>Reconnect</Button>
  {/if}
</div>
