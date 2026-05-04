/**
 * Stub for $app/state in browser test environment.
 */
export const page = {
  url: new URL("http://localhost"),
  params: {},
  route: { id: "" },
  status: 200,
  error: null,
  data: {},
  form: null,
  state: {},
};

export const navigating = null;

export const updated = {
  current: false,
  check: () => Promise.resolve(false),
};
