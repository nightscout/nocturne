---
name: frontend-testing
description: "Use when writing, editing, or reviewing frontend tests in the Nocturne Web app (src/Web/packages/app). Covers vitest-browser-svelte component tests, Node unit tests, and Playwright e2e tests. Triggers on .test.ts, .svelte.test.ts, vitest, test patterns, testing questions."
---

# Frontend Testing Skill

## Unbreakable Rules

- Always use locators (`page.getBy*()`) in browser tests - never containers from `vitest-browser-svelte`
- Use `.first()`, `.nth()`, `.last()` for multiple elements to avoid strict mode violations
- Always use `untrack()` when accessing `$derived` values in tests
- Use real FormData/Request objects in server tests - minimal mocking only
- Co-locate tests with components (`.svelte.test.ts` for browser, `.test.ts` for node)
- Follow naming conventions: kebab-case files, snake_case variables
- Start tests with "Foundation First" approach using `.skip` blocks for planning
- Never click SvelteKit form submit buttons in browser tests - test state directly
- Use `await expect.element()` for all locator assertions in browser tests
- Never mock the API client - use remote functions as the testing boundary

## Test Architecture

```
src/Web/packages/app/
├── vitest.workspace.ts           # Loads both configs
├── vitest.config.ts              # Node environment (unit + server tests)
├── vitest.browser.config.ts      # Chromium via Playwright (component tests)
├── src/
│   ├── lib/components/
│   │   ├── my-component.svelte
│   │   ├── my-component.svelte.test.ts    # Browser component test
│   │   └── my-component.test.ts           # Node unit test (if needed)
│   └── lib/server/
│       └── auth.test.ts                   # Server-side unit test
└── e2e/
    └── demo.test.ts                       # Playwright e2e test
```

### File naming

| Test type | Suffix | Runner | When to use |
|---|---|---|---|
| Browser component | `.svelte.test.ts` | vitest-browser-svelte + Chromium | Testing rendered Svelte components |
| Node unit | `.test.ts` | vitest (Node/happy-dom) | Pure logic, server code, utilities |
| E2E | `e2e/*.test.ts` | Playwright | Full user workflows across pages |

## Commands

```bash
cd src/Web/packages/app

# Run tests
pnpm test:unit          # Node unit tests
pnpm test:browser       # Browser component tests (Chromium)
pnpm test:e2e           # Playwright e2e tests
pnpm test               # All tests

# Lint after changes
pnpm lint
```

## Browser Component Tests (`.svelte.test.ts`)

### Basic pattern

```typescript
import { render } from "vitest-browser-svelte";
import { page } from "@vitest/browser/context";
import { describe, it, expect } from "vitest";
import MyComponent from "./my-component.svelte";

describe("MyComponent", () => {
  it("renders the title", async () => {
    render(MyComponent, { props: { title: "Hello" } });

    // Always use page.getBy* locators, never container queries
    await expect.element(page.getByText("Hello")).toBeVisible();
  });

  it("handles multiple matching elements", async () => {
    render(MyComponent, { props: { items: ["a", "b", "c"] } });

    // Use .first()/.nth()/.last() to avoid strict mode violations
    await expect.element(page.getByRole("listitem").first()).toBeVisible();
  });
});
```

### Locator priority (prefer top to bottom)

1. `page.getByRole("button", { name: "Submit" })` - accessible role + name
2. `page.getByLabel("Email")` - form inputs
3. `page.getByText("Hello")` - visible text
4. `page.getByTestId("chart")` - last resort

### Testing reactive state with `$derived`

```typescript
import { untrack } from "svelte";
import { flushSync } from "svelte";

it("updates derived value", async () => {
  const result = render(MyComponent, { props: { count: 0 } });

  // Always use untrack() when reading $derived values in tests
  const value = untrack(() => result.component.derivedValue);
  expect(value).toBe(0);

  // After state changes, flush and re-read
  flushSync();
});
```

### What NOT to do in browser tests

```typescript
// BAD: Using container queries
const { container } = render(MyComponent);
container.querySelector(".my-class"); // Never do this

// GOOD: Use locators
page.getByRole("button", { name: "Save" });

// BAD: Clicking form submit buttons (SvelteKit intercepts)
await page.getByRole("button", { type: "submit" }).click();

// GOOD: Test the resulting state directly
await expect.element(page.getByText("Saved")).toBeVisible();

// BAD: expect() without .element() for locators
expect(page.getByText("Hello")).toBeVisible(); // Wrong!

// GOOD: Always await expect.element()
await expect.element(page.getByText("Hello")).toBeVisible();
```

## Node Unit Tests (`.test.ts`)

### Server-side testing with real objects

```typescript
import { describe, it, expect } from "vitest";

it("parses form data correctly", async () => {
  // Use real FormData, not mocks
  const formData = new FormData();
  formData.set("email", "test@example.com");
  formData.set("name", "Test User");

  // Use real Request objects
  const request = new Request("http://localhost/api/submit", {
    method: "POST",
    body: formData,
  });

  const result = await parseSubmission(request);
  expect(result.email).toBe("test@example.com");
});
```

### What to mock, what not to mock

| Mock | Don't mock |
|---|---|
| External HTTP services | FormData, Request, Response |
| Time/dates (vi.useFakeTimers) | Zod schemas |
| Random values | URL parsing |

Never mock the Nocturne API client (`apiClient`). Tests that need API data should either:
1. Test the component with pre-resolved props (browser tests)
2. Test the remote function's transformation logic in isolation (node tests)

## Foundation First Approach

When writing a new test file, start by planning coverage with `.skip` blocks:

```typescript
describe("AlarmProfile", () => {
  it.skip("renders default alarm thresholds");
  it.skip("validates high threshold > low threshold");
  it.skip("saves profile on form submission");
  it.skip("shows error for invalid time range");
  it.skip("disables save button when form is invalid");
});
```

Then implement one test at a time, removing `.skip` as you go. This prevents scope creep and ensures comprehensive coverage before writing any test logic.

## Path Aliases

All test configs share these aliases:

| Alias | Path |
|---|---|
| `$lib` | `src/lib` |
| `$api` | `src/lib/api` |
| `$api-clients` | `src/lib/api-clients` |
| `$routes` | `src/routes` |

## Checklist

Before finishing any testing task:

1. Tests use locators, not container queries (browser tests)
2. Multiple-element locators use `.first()` / `.nth()` / `.last()`
3. All locator assertions use `await expect.element()`
4. No mocked API client - remote functions are the boundary
5. Tests are co-located with the code they test
6. `pnpm lint` passes after changes
