<script lang="ts">
  import { Button } from "$lib/components/ui/button";
  import * as Card from "$lib/components/ui/card";
  import { Input } from "$lib/components/ui/input";
  import { Label } from "$lib/components/ui/label";
  import {
    Loader2,
    UserPlus,
    Mail,
    Lock,
    User,
    CheckCircle,
    ArrowLeft,
  } from "lucide-svelte";
  import { registerForm, getLocalAuthConfig } from "../auth.remote";
  import { page } from "$app/state";
  import { goto } from "$app/navigation";

  // Query for local auth configuration
  const localAuthQuery = getLocalAuthConfig();

  // Get return URL from query params
  const returnUrl = $derived(page.url.searchParams.get("returnUrl") || "/");

  // Track registration result
  let registrationResult = $state<{
    success: boolean;
    requiresEmailVerification: boolean;
    requiresAdminApproval: boolean;
    message?: string;
  } | null>(null);

  // Handle form result
  $effect(() => {
    const result = registerForm.result;
    if (result) {
      registrationResult = {
        success: result.success,
        requiresEmailVerification: result.requiresEmailVerification ?? false,
        requiresAdminApproval: result.requiresAdminApproval ?? false,
        message: result.message,
      };
    }
  });

  function goToLogin() {
    goto(
      `/auth/login?returnUrl=${encodeURIComponent(returnUrl)}&registered=true`
    );
  }
</script>

<svelte:head>
  <title>Sign Up - Nocturne</title>
</svelte:head>

<svelte:boundary>
  {#snippet failed(error)}
    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Card.Root class="w-full max-w-md">
        <Card.Header class="text-center">
          <Card.Title class="text-2xl font-bold text-destructive">
            Error
          </Card.Title>
        </Card.Header>
        <Card.Content>
          <div
            class="rounded-md bg-destructive/10 p-4 text-sm text-destructive"
          >
            {error.message}
          </div>
          <Button class="mt-4 w-full" onclick={() => window.location.reload()}>
            Try Again
          </Button>
        </Card.Content>
      </Card.Root>
    </div>
  {/snippet}

  {#if localAuthQuery.loading}
    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Loader2 class="h-8 w-8 animate-spin text-primary" />
    </div>
  {:else}
    {@const localAuth = localAuthQuery.current}
    {@const hasLocalAuth = localAuth?.enabled ?? false}
    {@const allowRegistration = localAuth?.allowRegistration ?? false}
    {@const passwordRequirements = localAuth?.passwordRequirements}

    <div
      class="flex min-h-screen items-center justify-center bg-background p-4"
    >
      <Card.Root class="w-full max-w-md">
        <Card.Header class="space-y-1 text-center">
          <div
            class="mx-auto mb-4 flex h-12 w-12 items-center justify-center rounded-full bg-primary/10"
          >
            {#if registrationResult?.success}
              <CheckCircle class="h-6 w-6 text-green-600" />
            {:else}
              <UserPlus class="h-6 w-6 text-primary" />
            {/if}
          </div>
          <Card.Title class="text-2xl font-bold">
            {#if registrationResult?.success}
              Registration Successful
            {:else}
              Create an Account
            {/if}
          </Card.Title>
          <Card.Description>
            {#if registrationResult?.success}
              {#if registrationResult.requiresEmailVerification}
                Please check your email to verify your account.
              {:else if registrationResult.requiresAdminApproval}
                Your account is pending admin approval.
              {:else}
                Your account has been created. You can now sign in.
              {/if}
            {:else}
              Enter your information to create your Nocturne account
            {/if}
          </Card.Description>
        </Card.Header>

        <Card.Content class="space-y-4">
          {#if !hasLocalAuth}
            <div
              class="rounded-lg border border-yellow-200 bg-yellow-50 p-4 dark:border-yellow-900/50 dark:bg-yellow-900/20"
            >
              <p class="text-sm text-yellow-800 dark:text-yellow-200">
                Local authentication is not enabled. Please contact your
                administrator.
              </p>
            </div>
            <Button variant="outline" class="w-full" href="/auth/login">
              <ArrowLeft class="mr-2 h-4 w-4" />
              Back to Login
            </Button>
          {:else if !allowRegistration}
            <div
              class="rounded-lg border border-yellow-200 bg-yellow-50 p-4 dark:border-yellow-900/50 dark:bg-yellow-900/20"
            >
              <p class="text-sm text-yellow-800 dark:text-yellow-200">
                New user registration is currently disabled. Please contact your
                administrator for an account.
              </p>
            </div>
            <Button variant="outline" class="w-full" href="/auth/login">
              <ArrowLeft class="mr-2 h-4 w-4" />
              Back to Login
            </Button>
          {:else if registrationResult?.success}
            <!-- Success state -->
            <div
              class="rounded-md bg-green-50 dark:bg-green-900/20 p-4 text-sm text-green-800 dark:text-green-200"
            >
              {#if registrationResult.requiresEmailVerification}
                <p class="font-medium mb-2">Check your email</p>
                <p>
                  We've sent a verification link to your email address. Please
                  click the link to activate your account.
                </p>
              {:else if registrationResult.requiresAdminApproval}
                <p class="font-medium mb-2">Pending Approval</p>
                <p>
                  Your registration has been submitted. An administrator will
                  review your account shortly. You'll receive an email once
                  approved.
                </p>
              {:else}
                <p>
                  {registrationResult.message ||
                    "Your account has been created successfully!"}
                </p>
              {/if}
            </div>

            {#if !registrationResult.requiresEmailVerification && !registrationResult.requiresAdminApproval}
              <Button class="w-full" onclick={goToLogin}>
                Continue to Sign In
              </Button>
            {:else}
              <Button variant="outline" class="w-full" href="/auth/login">
                <ArrowLeft class="mr-2 h-4 w-4" />
                Back to Login
              </Button>
            {/if}
          {:else}
            <!-- Registration form -->
            <form {...registerForm} class="space-y-4">
              <input
                type="hidden"
                {...registerForm.fields.returnUrl.as("text")}
                value={returnUrl}
              />

              <div class="space-y-2">
                <Label for="displayName">Display Name</Label>
                <div class="relative">
                  <User
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...registerForm.fields.displayName.as("text")}
                    id="displayName"
                    placeholder="Your name (optional)"
                    class="pl-10"
                    disabled={!!registerForm.pending}
                  />
                </div>
                {#each registerForm.fields.displayName.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
              </div>

              <div class="space-y-2">
                <Label for="email">
                  Email <span class="text-destructive">*</span>
                </Label>
                <div class="relative">
                  <Mail
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...registerForm.fields.email.as("email")}
                    id="email"
                    placeholder="you@example.com"
                    class="pl-10"
                    required
                    disabled={!!registerForm.pending}
                  />
                </div>
                {#each registerForm.fields.email.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
              </div>

              <div class="space-y-2">
                <Label for="password">
                  Password <span class="text-destructive">*</span>
                </Label>
                <div class="relative">
                  <Lock
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...registerForm.fields._password.as("password")}
                    id="password"
                    placeholder="••••••••••••"
                    class="pl-10"
                    required
                    disabled={!!registerForm.pending}
                  />
                </div>
                {#each registerForm.fields._password.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
                {#if passwordRequirements}
                  <p class="text-xs text-muted-foreground">
                    Minimum {passwordRequirements.minLength} characters
                  </p>
                {/if}
              </div>

              <div class="space-y-2">
                <Label for="confirmPassword">
                  Confirm Password <span class="text-destructive">*</span>
                </Label>
                <div class="relative">
                  <Lock
                    class="absolute left-3 top-1/2 h-4 w-4 -translate-y-1/2 text-muted-foreground"
                  />
                  <Input
                    {...registerForm.fields.confirmPassword.as("password")}
                    id="confirmPassword"
                    placeholder="••••••••••••"
                    class="pl-10"
                    required
                    disabled={!!registerForm.pending}
                  />
                </div>
                {#each registerForm.fields.confirmPassword.issues() as issue}
                  <p class="text-sm text-destructive">{issue.message}</p>
                {/each}
              </div>

              {#each registerForm.fields.allIssues() as issue}
                <div
                  class="rounded-md bg-destructive/10 p-3 text-sm text-destructive"
                >
                  {issue.message}
                </div>
              {/each}

              <Button
                type="submit"
                class="w-full"
                disabled={!!registerForm.pending}
              >
                {#if registerForm.pending}
                  <Loader2 class="mr-2 h-4 w-4 animate-spin" />
                  Creating account...
                {:else}
                  Create Account
                {/if}
              </Button>
            </form>

            <p class="text-center text-sm text-muted-foreground">
              Already have an account?
              <a
                href="/auth/login?returnUrl={encodeURIComponent(returnUrl)}"
                class="font-medium text-primary hover:underline"
              >
                Sign in
              </a>
            </p>
          {/if}

          <div class="text-center text-xs text-muted-foreground">
            <p>
              By creating an account, you agree to our
              <a href="/terms" class="underline hover:text-foreground">
                Terms of Service
              </a>
              and
              <a href="/privacy" class="underline hover:text-foreground">
                Privacy Policy
              </a>
            </p>
          </div>
        </Card.Content>
      </Card.Root>
    </div>
  {/if}
</svelte:boundary>
