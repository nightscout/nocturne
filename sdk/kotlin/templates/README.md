# Kotlin SDK template overrides

Both files are the stock templates from openapi-generator **v7.21.0**
(`modules/openapi-generator/src/main/resources/kotlin-client/`) with minimal,
documented changes. Together with `../.openapi-generator-ignore` they remove
Apache Oltu, so the generated project under `sdk/kotlin/out` needs no mutation
before building (the publish overlay is still appended by `sdk-publish.yml`).

## Apache Oltu

`openapi-v4.json` carries an OAuth2 security scheme, which makes the stock
`jvm-retrofit2` library emit `auth/OAuth.kt` and `auth/OAuthOkHttpClient.kt`,
OAuth branches in `infrastructure/ApiClient.kt`, and declare
`org.apache.oltu.oauth2:org.apache.oltu.oauth2.client:1.0.2` in `build.gradle`.
Oltu is retired to the Apache Attic and 1.0.2 (2016) is its last release, so the
SDK does not ship it.

`../.openapi-generator-ignore` skips the two `auth/` files, and the overrides
below drop the rest. `auth/HttpBearerAuth.kt` is still generated, so
`ApiClient(authName = "bearer", bearerToken = ...)` and `setBearerToken` keep
working; consumers obtain tokens themselves. `auth/OAuthFlow.kt` (a plain enum)
is still generated and unused.

The `publish-kotlin` job in `sdk-publish.yml` fails if any generated file
mentions `org.apache.oltu`, and its Gradle build proves the sources compile
without it.

## `build.gradle.mustache`

Removes the `{{#hasOAuthMethods}}` block declaring the Oltu dependency.

## `libraries/jvm-retrofit2/infrastructure/ApiClient.kt.mustache`

Removes every `{{#hasOAuthMethods}}` block (the Oltu imports, the client
ID/secret/username/password constructor, the OAuth branch of `setCredentials`,
`getTokenEndPoint`, `getAuthorizationEndPoint`, `setAccessToken`,
`configureAuthorizationFlow` and `registerAccessTokenListener`) and the
`{{#isOAuth}}` entry that built an `OAuth` interceptor. An `oauth2` auth name now
fails the `else` branch of the constructor instead of building an interceptor.

## Refreshing after a generator upgrade

When bumping the pinned `openapitools/openapi-generator-cli` version:

1. Fetch the new tag's stock templates from
   `https://raw.githubusercontent.com/OpenAPITools/openapi-generator/<tag>/modules/openapi-generator/src/main/resources/kotlin-client/`
2. Re-apply the changes above (diff against the previous stock version to see
   exactly what was changed).
3. The Oltu check in `sdk-publish.yml` catches a returning Oltu reference.
   Check whether the generator emits any new Oltu-backed file that needs an
   entry in `../.openapi-generator-ignore`.
