<script lang="ts">
  import { page } from "$app/state";
  import { goto, invalidateAll } from "$app/navigation";
  import { changePasswordForm, getLocalAuthConfig } from "../auth.remote";
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import { Loader2, KeyRound, Eye, EyeOff, Lock } from "lucide-svelte";

  const required = $derived(page.url.searchParams.get("required") === "true");
  const returnUrl = $derived(page.url.searchParams.get("returnUrl") || "/");

  const config = getLocalAuthConfig();

  let showCurrentPassword = $state(false);
  let showPassword = $state(false);
  let showConfirmPassword = $state(false);
</script>

<svelte:head>
  <title>Change Password | Nocturne</title>
</svelte:head>

<svelte:boundary>
  {#snippet failed(error, reset)}
    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Card.Root class="w-full max-w-md">
        <Card.Header class="text-center">
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-destructive/10"
          >
            <KeyRound class="h-6 w-6 text-destructive" />
          </div>
          <Card.Title class="text-2xl font-bold text-destructive">
            Something went wrong
          </Card.Title>
          <Card.Description>
            We couldn't process your request. Please try again.
          </Card.Description>
        </Card.Header>
        <Card.Content>
          <div
            class="rounded-md bg-destructive/10 p-4 text-sm text-destructive"
          >
            {(error as Error).message}
          </div>
          <Button class="mt-4 w-full" onclick={reset}>Try Again</Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/snippet}

  <div class="flex min-h-screen items-center justify-center bg-background p-4">
    <Card.Root class="w-full max-w-md">
      <Card.Header class="space-y-1 text-center">
        <div
          class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
        >
          <KeyRound class="h-6 w-6 text-primary" />
        </div>
        <Card.Title class="text-2xl font-bold">Change Your Password</Card.Title>
        <Card.Description>
          {#if required}
            You need to update your password before continuing.
          {:else}
            Update your password to keep your account secure.
          {/if}
        </Card.Description>
      </Card.Header>

      <Card.Content class="space-y-4">
        <form
          {...changePasswordForm.enhance(async ({ submit }) => {
            await submit();
            const result = changePasswordForm.result as
              | { success?: boolean; returnUrl?: string }
              | undefined;
            if (result?.success) {
              const targetUrl = result.returnUrl || returnUrl;
              await invalidateAll();
              await goto(targetUrl, { invalidateAll: true });
            }
          })}
          class="space-y-4"
        >
          <input
            type="hidden"
            name={changePasswordForm.fields.returnUrl.as("text").name}
            value={returnUrl}
          />

          {#each changePasswordForm.fields.allIssues() as issue}
            <div
              class="rounded-md bg-destructive/10 p-3 text-sm text-destructive"
            >
              {issue.message}
            </div>
          {/each}

          <div class="space-y-2">
            <Label for="currentPassword">Current Password</Label>
            <div class="relative">
              <Lock
                class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              />
              <Input
                {...changePasswordForm.fields.currentPassword.as("password")}
                id="currentPassword"
                type={showCurrentPassword ? "text" : "password"}
                autocomplete="current-password"
                required
                placeholder="Enter your current password"
                class="pl-10 pr-10"
                disabled={!!changePasswordForm.pending}
              />
              <button
                type="button"
                onclick={() => (showCurrentPassword = !showCurrentPassword)}
                class="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
              >
                {#if showCurrentPassword}
                  <EyeOff class="h-4 w-4" />
                {:else}
                  <Eye class="h-4 w-4" />
                {/if}
              </button>
            </div>
            {#each changePasswordForm.fields.currentPassword.issues() as issue}
              <p class="text-sm text-destructive">{issue.message}</p>
            {/each}
          </div>

          <div class="space-y-2">
            <Label for="password">New Password</Label>
            <div class="relative">
              <Lock
                class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              />
              <Input
                {...changePasswordForm.fields._password.as("password")}
                id="password"
                type={showPassword ? "text" : "password"}
                autocomplete="new-password"
                required
                placeholder="Enter your new password"
                class="pl-10 pr-10"
                disabled={!!changePasswordForm.pending}
              />
              <button
                type="button"
                onclick={() => (showPassword = !showPassword)}
                class="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
              >
                {#if showPassword}
                  <EyeOff class="h-4 w-4" />
                {:else}
                  <Eye class="h-4 w-4" />
                {/if}
              </button>
            </div>
            {#each changePasswordForm.fields._password.issues() as issue}
              <p class="text-sm text-destructive">{issue.message}</p>
            {/each}
            {#if changePasswordForm.fields._password.issues()?.length === 0}
              <p class="text-xs text-muted-foreground">
                {#if config.loading}
                  Loading password requirements...
                {:else if config.current}
                  Must be at least {config.current.passwordRequirements
                    ?.minLength ?? 12} characters
                {:else}
                  Must be at least 12 characters
                {/if}
              </p>
            {/if}
          </div>

          <div class="space-y-2">
            <Label for="confirmPassword">Confirm Password</Label>
            <div class="relative">
              <Lock
                class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
              />
              <Input
                {...changePasswordForm.fields.confirmPassword.as("password")}
                id="confirmPassword"
                type={showConfirmPassword ? "text" : "password"}
                autocomplete="new-password"
                required
                placeholder="Confirm your new password"
                class="pl-10 pr-10"
                disabled={!!changePasswordForm.pending}
              />
              <button
                type="button"
                onclick={() => (showConfirmPassword = !showConfirmPassword)}
                class="absolute right-3 top-1/2 -translate-y-1/2 text-muted-foreground hover:text-foreground"
              >
                {#if showConfirmPassword}
                  <EyeOff class="h-4 w-4" />
                {:else}
                  <Eye class="h-4 w-4" />
                {/if}
              </button>
            </div>
            {#each changePasswordForm.fields.confirmPassword.issues() as issue}
              <p class="text-sm text-destructive">{issue.message}</p>
            {/each}
          </div>

          <Button
            type="submit"
            class="w-full"
            disabled={!!changePasswordForm.pending}
          >
            {#if changePasswordForm.pending}
              <Loader2 class="mr-2 h-4 w-4 animate-spin" />
              Updating...
            {:else}
              Update Password
            {/if}
          </Button>
        </form>

        <div class="text-center">
          <a
            href="/auth/login"
            class="text-sm text-muted-foreground hover:text-foreground"
          >
            Back to login
          </a>
        </div>
      </Card.Content>
    </Card.Root>
  </div>
</svelte:boundary>
