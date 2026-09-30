#[cfg(feature = "native")]
mod benchmark;
#[cfg(feature = "native")]
mod cancel_probe;
mod corpus;
#[cfg(feature = "native")]
mod load_probe;
mod scoring;

use std::error::Error;
use std::path::Path;

fn execute() -> Result<(), Box<dyn Error>> {
    let args: Vec<String> = std::env::args().skip(1).collect();
    match args.first().map(String::as_str) {
        Some("validate") if args.len() == 2 => {
            let corpus = corpus::load(Path::new(&args[1]))?;
            println!("Validated {} fixtures; no inference was performed", corpus.len());
            Ok(())
        }
        #[cfg(feature = "native")]
        Some("asr") if args.len() == 6 => {
            benchmark::run(Path::new(&args[1]), Path::new(&args[2]), Path::new(&args[3]), args[4].parse()?, &args[5])
        }
        #[cfg(feature = "native")]
        Some("load") if args.len() == 11 => {
            load_probe::run(&args[1..])
        }
        #[cfg(feature = "native")]
        Some("cancel") if args.len() == 7 => {
            cancel_probe::run(Path::new(&args[1]), Path::new(&args[2]), Path::new(&args[3]), args[4].parse()?, &args[5], args[6].parse()?)
        }
        #[cfg(feature = "native")]
        Some("cancel-child") if args.len() == 8 => {
            cancel_probe::child(Path::new(&args[1]), Path::new(&args[2]), &args[3], args[4].parse()?, &args[5], &args[6], Path::new(&args[7]))
        }
        _ => Err("Usage: echosub-model-probe validate <fixtures.json>\nNative feature: asr <fixtures.json> <model-downloads.json> <report.json> <threads> <cpu|cuda|metal>\nNative feature: cancel <fixtures.json> <model-downloads.json> <report.json> <threads> <cpu|cuda|metal> <iterations 10..100>".into()),
    }
}

fn main() {
    if let Err(error) = execute() {
        eprintln!("model probe: {error}");
        std::process::exit(1);
    }
}
