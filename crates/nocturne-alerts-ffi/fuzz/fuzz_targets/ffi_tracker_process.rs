#![no_main]

libfuzzer_sys::fuzz_target!(|data: &[u8]| {
    nocturne_alerts_fuzz::check_entry(nocturne_alerts::nocturne_alerts_tracker_process, data);
});
