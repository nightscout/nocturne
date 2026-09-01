# Java SDK template overrides

All three files are the stock templates from openapi-generator **v7.21.0**
(`modules/openapi-generator/src/main/resources/Java/libraries/okhttp-gson/`)
with minimal, documented changes. Together with `../.openapi-generator-ignore`
they replace all post-generation shell edits — the generated project under
`sdk/java/out` needs no mutation before building.

## `libraries/okhttp-gson/ApiClient.mustache`

Every `RequestBody.create(content, mediaType)` call site (9 of them) is
swapped to the `RequestBody.create(mediaType, content)` argument order.

The MediaType-first order exists in OkHttp 3.x and is retained (deprecated
but functional) in 4.x, so the generated client is bytecode-compatible with
both majors. The content-first order the stock template uses only exists in
OkHttp 4.x. Android consumers — xDrip pins OkHttp 3.12.13 — exclude this
SDK's OkHttp 4.x transitive and supply their own 3.12.x at runtime, which
only works if the generated code sticks to the shared API surface.

The "Verify OkHttp 3.12 floor compatibility" step in
`.github/workflows/sdk-publish.yml` compiles the generated sources against
OkHttp 3.12.13 / gson 2.8.6 and fails the publish if this guarantee breaks.

Note: `okHttpVersion` in `config.yaml` is **not** a supported
openapi-generator option — it was silently ignored (versions ≤ 0.2.3
shipped requiring OkHttp 4.12.0 despite it). The template override plus the
CI gate is what actually provides 3.x compatibility.

## `libraries/okhttp-gson/auth/OAuthOkHttpClient.mustache`

The single `RequestBody.create(content, mediaType)` call site is swapped to the
`RequestBody.create(mediaType, content)` order, for the reason above.

This file is only generated when the spec carries an OAuth2 security scheme,
which `openapi-v4.json` gained after v0.2.4 — so the `RequestBody` guarantee
was reached by a source file that no override covered, and the floor gate
caught it.

The same scheme also generates `auth/RetryingOAuth.java` and OAuth branches in
`ApiClient.java`. Those need no override, but all three import Apache Oltu, so
the floor gate's classpath carries `org.apache.oltu.oauth2.client` (declared by
the generated `build.gradle`) and its `org.apache.oltu.oauth2.common`
transitive.

## `libraries/okhttp-gson/build.gradle.mustache`

Two changes:

1. Removes the `jakarta.ws.rs-api` and `commons-lang3` dependencies —
   declared by the stock template but referenced by zero generated sources.
2. Appends a guarded `apply from: '../publish.gradle'` so the Maven Central
   publishing/signing overlay is picked up whenever the project is generated
   into its repo location (`sdk/java/out`), with no build-file editing in CI.

A stale fork of this template fails loudly: missing dependencies break the
gradle build, and the publish overlay guard only affects publishing config.

## Refreshing after a generator upgrade

When bumping the pinned `openapitools/openapi-generator-cli` version:

1. Fetch the new tag's stock templates from
   `https://raw.githubusercontent.com/OpenAPITools/openapi-generator/<tag>/modules/openapi-generator/src/main/resources/Java/libraries/okhttp-gson/`
2. Re-apply the changes above (diff against the previous stock version to
   see exactly what was changed).
3. The floor-compatibility CI step catches a missed `RequestBody.create`
   swap; a missed build.gradle change surfaces as a gradle build failure.
4. Diff the new stock `build.gradle.mustache`'s dependency block against the
   floor step's classpath in `sdk-publish.yml`. A generator bump can change a
   dependency version or add one; the two lists are maintained by hand and
   nothing checks that they agree. A stale entry fails loudly (missing
   package), never silently.
