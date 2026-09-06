//! Standard in-crate `uniffi-bindgen` binary (see the UniFFI user guide,
//! "Foreign-language bindings"): generates the Kotlin bindings from the
//! compiled library. Gated on the `bindgen` cargo feature; the exact commands
//! are documented in this crate's `README.md` under "Kotlin (UniFFI)".

fn main() {
    uniffi::uniffi_bindgen_main()
}
