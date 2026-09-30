#[cfg(windows)]
mod owner;
#[cfg(windows)]
mod probe;
#[cfg(windows)]
pub use owner::{CaptureOwner, Info, Stats, FRAME_QUEUE, MAX_PACKET_SAMPLES, PACKET_SLOTS};
#[cfg(windows)]
pub fn run_probe() -> Result<(), Box<dyn std::error::Error>> {
    probe::run()
}
