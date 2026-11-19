#!/usr/bin/env bash
# Fails when a generated Java model rejects an unknown response field or an unknown enum value.
# Usage: verify-tolerant-java-models.sh <generated-java-project-dir>
set -euo pipefail

models="${1:?Usage: verify-tolerant-java-models.sh <generated-java-project-dir>}/src/main/java/org/nightscoutfoundation/nocturne/model"

if ! compgen -G "$models/*.java" > /dev/null; then
  echo "No generated models under $models" >&2
  exit 1
fi

failed=0

# The stock templates throw on an unknown field ("not defined in the") and on an unknown enum
# value ("Unexpected value"), in every model, inner enum and one-of wrapper.
if grep -rlE 'not defined in the|Unexpected value' "$models"; then
  echo "Files above throw on an unknown response field or enum value. Check that sdk/filter-v4-spec.js removes additionalProperties: false and that sdk/java/config.yaml sets disallowAdditionalPropertiesIfNotPresent: false and enumUnknownDefaultCase: true." >&2
  failed=1
fi

# Positive checks, so a reworded stock message cannot hide a regression. String enums fall back to
# UNKNOWN_DEFAULT_OPEN_API and integer enums to NUMBER_unknown_default_open_api.
enums="$(grep -rlE 'static [A-Za-z]+ fromValue\(' "$models" || true)"
if [ -z "$enums" ]; then
  echo "No enums found under $models." >&2
  failed=1
else
  missing="$(echo "$enums" | xargs grep -Li 'unknown_default_open_api' || true)"
  if [ -n "$missing" ]; then
    echo "$missing"
    echo "Enums above have no unknown-value fallback. Check that sdk/java/config.yaml sets enumUnknownDefaultCase: true." >&2
    failed=1
  fi
fi

# Gson would serialize a non-transient holder as an unknownProperties member of every request body.
holders="$(grep -rlF 'putUnknownProperty(' "$models" || true)"
if [ -z "$holders" ]; then
  echo "No model keeps unknown response fields. Check disallowAdditionalPropertiesIfNotPresent in sdk/java/config.yaml and the overrides in sdk/java/templates." >&2
  failed=1
else
  loose="$(echo "$holders" | xargs grep -LF 'private transient Map<String, Object> unknownProperties;' || true)"
  if [ -n "$loose" ]; then
    echo "$loose"
    echo "Models above hold the unknown-field holder without transient. Check sdk/java/templates/libraries/okhttp-gson/additional_properties.mustache." >&2
    failed=1
  fi
fi

# The stock write adapter removes the additionalProperties key, which deletes the real property of
# that name from every request body of the models that have one.
if grep -rlF 'obj.remove("additionalProperties")' "$models"; then
  echo "Files above drop a real additionalProperties property when writing. Check the write adapter in sdk/java/templates/libraries/okhttp-gson/pojo.mustache." >&2
  failed=1
fi

exit "$failed"
