import { form as kitForm } from "$app/server";
import { isStandardSchema, tolerateKitFormKey } from "./kit-form-key";

export { command, getRequestEvent, query, requested } from "$app/server";

/** `form()` from `$app/server`, its schema read through {@link tolerateKitFormKey}. */
export const form: typeof kitForm = (...args: unknown[]) =>
  Reflect.apply(
    kitForm,
    undefined,
    isStandardSchema(args[0]) ? [tolerateKitFormKey(args[0]), ...args.slice(1)] : args
  );
