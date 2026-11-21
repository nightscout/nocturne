#!/usr/bin/env bash
set -euo pipefail

if [ "$(uname -s)" != "Darwin" ]; then
  echo "build-xcframework.sh needs macOS: xcodebuild, lipo and the Apple SDKs exist nowhere else." >&2
  exit 1
fi

for tool in cargo rustup lipo xcodebuild; do
  command -v "$tool" >/dev/null || { echo "$tool is not on PATH." >&2; exit 1; }
done

crate_dir="$(cd "$(dirname "$0")/.." && pwd)"
crates_dir="$(cd "$crate_dir/.." && pwd)"
out="$crates_dir/target/swift"
package_dir="$crate_dir/swift"
xcframework="NocturneAlertsFFI.xcframework"

export IPHONEOS_DEPLOYMENT_TARGET=15.0
export MACOSX_DEPLOYMENT_TARGET=13.0

cd "$crates_dir"
unset CARGO_TARGET_DIR

targets=(
  aarch64-apple-ios
  aarch64-apple-ios-sim
  x86_64-apple-ios
  aarch64-apple-darwin
  x86_64-apple-darwin
)
rustup target add "${targets[@]}"

lib=libnocturne_alerts.a
for target in "${targets[@]}"; do
  case "$target" in
    *-apple-darwin)
      cargo build --release -p nocturne-alerts-ffi --features uniffi --lib --target "$target"
      ;;
    *)
      cargo rustc --release -p nocturne-alerts-ffi --features uniffi --lib --target "$target" --crate-type staticlib
      ;;
  esac
done

rm -rf "$out"
mkdir -p "$out/bindings" "$out/headers/nocturne_alertsFFI" "$out/lipo/ios-sim" "$out/lipo/macos"

# Library mode reads the UniFFI metadata out of the macOS arm64 library; the checksums depend on the
# crate revision and features, which every slice shares.
cargo run -p nocturne-alerts-ffi --features bindgen --bin uniffi-bindgen -- \
  generate --library target/aarch64-apple-darwin/release/libnocturne_alerts.dylib \
  --language swift --out-dir "$out/bindings"

cp "$out/bindings/nocturne_alertsFFI.h" "$out/headers/nocturne_alertsFFI/"
cp "$out/bindings/nocturne_alertsFFI.modulemap" "$out/headers/nocturne_alertsFFI/module.modulemap"

lipo -create \
  "target/aarch64-apple-ios-sim/release/$lib" \
  "target/x86_64-apple-ios/release/$lib" \
  -output "$out/lipo/ios-sim/$lib"
lipo -create \
  "target/aarch64-apple-darwin/release/$lib" \
  "target/x86_64-apple-darwin/release/$lib" \
  -output "$out/lipo/macos/$lib"

xcodebuild -create-xcframework \
  -library "target/aarch64-apple-ios/release/$lib" -headers "$out/headers" \
  -library "$out/lipo/ios-sim/$lib" -headers "$out/headers" \
  -library "$out/lipo/macos/$lib" -headers "$out/headers" \
  -output "$out/$xcframework"

# SwiftPM refuses a binary target outside the package root, so the package gets its own copy.
rm -rf "${package_dir:?}/$xcframework"
cp -R "$out/$xcframework" "$package_dir/$xcframework"
mkdir -p "$package_dir/Sources/NocturneAlerts"
cp "$out/bindings/nocturne_alerts.swift" "$package_dir/Sources/NocturneAlerts/nocturne_alerts.swift"

echo "Built $out/$xcframework and refreshed $package_dir"
