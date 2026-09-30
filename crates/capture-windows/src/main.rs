#[cfg(windows)]
fn main() {
    if let Err(error) = echosub_capture_windows::run_probe() {
        eprintln!("capture probe: {error}");
        std::process::exit(1);
    }
}

#[cfg(not(windows))]
fn main() {
    eprintln!("WASAPI loopback probe requires Windows");
    std::process::exit(1);
}
