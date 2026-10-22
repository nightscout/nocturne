# Java SDK template overrides

Both files are the stock templates from openapi-generator **v7.21.0**
(`modules/openapi-generator/src/main/resources/Java/libraries/okhttp-gson/`)
with minimal, documented changes. Together with `../.openapi-generator-ignore`
they replace all post-generation shell edits — the generated project under
`sdk/java/out` needs no mutation before building.

## `libraries/okhttp-gson/ApiClient.mustache`

Two changes:

1. Every `RequestBody.create(content, mediaType)` call site (9 of them) is
   swapped to the `RequestBody.create(mediaType, content)` argument order.
2. The Apache Oltu paths are removed: the Oltu imports, the client-ID
   constructors that build a `RetryingOAuth`, and `getTokenEndPoint()`. See
   "Apache Oltu" below.

The MediaType-first order exists in OkHttp 3.x and is retained (deprecated
but functional) in 4.x, so the generated client is bytecode-compatible with
both majors. The content-first order the stock template uses only exists in
OkHttp 4.x. Android consumers — xDrip pins OkHttp 3.12.13 — exclude this
SDK's OkHttp 4.x transitive and supply their own 3.12.x at runtime, which
only works if the generated code sticks to the shared API surface.

The "Verify OkHttp 3.12 floor compatibility" step in
`.github/workflows/sdk-publish.yml` runs `./gradlew compileOkhttp3Floor`, which
fails the publish if this guarantee breaks. The task comes from
`build.gradle.mustache` (see below): it resolves the generated project's own
dependency graph with okhttp pinned to 3.12.13 and gson to 2.8.6, and compiles
`src/main/java` against it. The floor is a constraint on the real graph, so it
cannot drift from the `dependencies` block. Run
`./gradlew dependencies --configuration okhttp3Floor` in the generated project
to see what it resolved.

Note: `okHttpVersion` in `config.yaml` is **not** a supported
openapi-generator option — it was silently ignored (versions ≤ 0.2.3
shipped requiring OkHttp 4.12.0 despite it). The template override plus the
CI gate is what actually provides 3.x compatibility.

## Apache Oltu

`openapi-v4.json` carries an OAuth2 security scheme (added after v0.2.4), which makes
the stock generator emit `auth/RetryingOAuth.java`, `auth/OAuthOkHttpClient.java`
and OAuth branches in `ApiClient.java`, all importing Apache Oltu, and declare
`org.apache.oltu.oauth2.client:1.0.2` in `build.gradle`. Oltu is retired to the
Apache Attic and 1.0.2 (2016) is its last release, so the SDK does not ship it.
The generated `pom.xml` and `build.sbt` still declare it; neither is used to
publish, so they are not overridden.

`../.openapi-generator-ignore` skips the two `auth/` files, and the
`ApiClient.mustache` and `build.gradle.mustache` overrides drop the rest. The
plain `auth/OAuth.java` (a bearer-token holder, no Oltu) is still generated, so
`setAccessToken` keeps working; consumers obtain tokens themselves.

`compileOkhttp3Floor` also fails when the resolved `okhttp3Floor` graph contains
any `org.apache.oltu*` module or `org.json:json` (Oltu's transitive, which
duplicates the Android framework's `org.json`). A generator upgrade that
restores the Oltu dependency in `build.gradle` fails the publish this way, and
one that also restores an Oltu import fails it on the missing classes too.

## `libraries/okhttp-gson/build.gradle.mustache`

Four changes:

1. Removes the `jakarta.ws.rs-api` and `commons-lang3` dependencies —
   declared by the stock template but referenced by zero generated sources.
2. Removes the Apache Oltu dependency (see "Apache Oltu" above).
3. Adds the `okhttp3Floor` configuration (extends `implementation`, with okhttp
   and gson pinned down, resolving the `java-api` usage like `compileClasspath`)
   and the `compileOkhttp3Floor` task that compiles the main sources against it
   and rejects Oltu and `org.json:json` in the resolved graph.
4. Appends a guarded `apply from: '../publish.gradle'` so the Maven Central
   publishing/signing overlay is picked up whenever the project is generated
   into its repo location (`sdk/java/out`), with no build-file editing in CI.

A stale fork of this template fails loudly only where something is checked:
missing dependencies break the gradle build, and a restored Oltu dependency
fails `compileOkhttp3Floor`. A missed removal of `jakarta.ws.rs-api` or
`commons-lang3` fails nothing; the publish overlay guard only affects
publishing config.

## Refreshing after a generator upgrade

When bumping the pinned `openapitools/openapi-generator-cli` version:

1. Fetch the new tag's stock templates from
   `https://raw.githubusercontent.com/OpenAPITools/openapi-generator/<tag>/modules/openapi-generator/src/main/resources/Java/libraries/okhttp-gson/`
2. Re-apply the changes above (diff against the previous stock version to
   see exactly what was changed).
3. The floor-compatibility CI step (`compileOkhttp3Floor`) catches a missed `RequestBody.create`
   swap and a returning Oltu or `org.json:json` dependency. Check whether the generator emits any new
   Oltu-backed file that needs an entry in `../.openapi-generator-ignore`.
