#![no_main]

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    nocturne_alerts_fuzz::check_parse_payload(data);
});
