//! Export the bloom's catalogue donor or render an authored scene at selected simulation ticks.
//!
//! ```text
//! cargo run -p nocturne-watercolour-wasm --example bloom_frames --release -- donor <out.json> <sim_resolution>
//! cargo run -p nocturne-watercolour-wasm --example bloom_frames --release -- render <scene.json> <out_dir> <w> <h> <tick,tick,...>
//! ```

use std::fs;
use std::path::PathBuf;

use nocturne_watercolour_core::application::{Exporter, Playback, Renderer};
use nocturne_watercolour_core::domain::Seed;
use nocturne_watercolour_infra::document::parse_scene_json;
use nocturne_watercolour_infra::export::PngExporter;
use nocturne_watercolour_infra::gpu::{GpuContext, GpuEngine};
use nocturne_watercolour_wasm::scene_tools::{
    DEFAULT_INTENSITY, DetailLevel, Surface, catalogue_scene_json_with_resolution,
};

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("donor") => {
            let res: u32 = args[2].parse().expect("sim_resolution");
            let json = catalogue_scene_json_with_resolution(
                "avatar-wash",
                Seed(1),
                "slate",
                DEFAULT_INTENSITY,
                DetailLevel::Large,
                Surface::Light,
                Some(res),
            )
            .expect("donor");
            fs::write(&args[1], json).expect("write donor");
        }
        Some("render") => {
            let scene = parse_scene_json(&fs::read_to_string(&args[1]).expect("read scene"))
                .expect("parse scene");
            let out = PathBuf::from(&args[2]);
            fs::create_dir_all(&out).expect("out dir");
            let w: u32 = args[3].parse().expect("w");
            let h: u32 = args[4].parse().expect("h");
            let ticks: Vec<u32> = args[5].split(',').map(|t| t.parse().expect("tick")).collect();
            let ctx = GpuContext::try_new().expect("gpu").expect("no gpu adapter");
            let gpu = GpuEngine::new(ctx).expect("gpu engine");
            let mut pb = Playback::new(gpu, scene, 1000.0).expect("playback");
            for t in ticks {
                pb.seek_tick(t).expect("seek");
                let image = pb.simulator().render(w, h).expect("render");
                let bytes = PngExporter.encode(&image).expect("png");
                fs::write(out.join(format!("t{t:03}.png")), bytes).expect("write");
            }
        }
        _ => panic!("usage: bloom_frames donor|render ..."),
    }
}
