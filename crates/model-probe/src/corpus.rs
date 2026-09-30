use serde::Deserialize;
use sha2::{Digest, Sha256};
use std::collections::HashSet;
use std::error::Error;
use std::fs::File;
use std::io::Read;
use std::path::Path;

#[derive(Deserialize)]
pub struct Manifest {
    pub schema_version: u32,
    pub fixtures: Vec<Fixture>,
}

#[derive(Deserialize)]
pub struct Fixture {
    pub id: String,
    pub path: String,
    pub sha256: String,
    pub language: String,
    pub reference: String,
    pub source: String,
    pub usage: String,
    pub kind: String,
    pub speech_segments_ms: Vec<[u64; 2]>,
}

#[cfg_attr(not(feature = "native"), allow(dead_code))]
pub struct Input {
    pub fixture: Fixture,
    pub pcm: Vec<f32>,
}

pub fn sha256(path: &Path) -> Result<String, Box<dyn Error>> {
    let mut file = File::open(path)?;
    let mut digest = Sha256::new();
    let mut buffer = [0u8; 65536];
    loop {
        let count = file.read(&mut buffer)?;
        if count == 0 {
            break;
        }
        digest.update(&buffer[..count]);
    }
    Ok(format!("{:x}", digest.finalize()))
}

pub fn load(path: &Path) -> Result<Vec<Input>, Box<dyn Error>> {
    if std::fs::metadata(path)?.len() > 1024 * 1024 {
        return Err("manifest exceeds 1 MiB".into());
    }
    let data = std::fs::read(path)?;
    if data.len() > 1024 * 1024 {
        return Err("manifest exceeds 1 MiB".into());
    }
    let manifest: Manifest = serde_json::from_slice(&data)?;
    if manifest.schema_version != 1 || manifest.fixtures.is_empty() || manifest.fixtures.len() > 100
    {
        return Err("schema_version must be 1 with 1..100 fixtures".into());
    }
    let root = path.parent().unwrap_or(Path::new("."));
    let mut ids = HashSet::new();
    let mut inputs = Vec::new();
    let mut total_samples = 0usize;
    for fixture in manifest.fixtures {
        if fixture.id.is_empty() || !ids.insert(fixture.id.clone()) {
            return Err("fixture IDs must be nonempty and unique".into());
        }
        if !matches!(fixture.language.as_str(), "en" | "ja" | "ko")
            || fixture.source.is_empty()
            || fixture.usage.is_empty()
        {
            return Err(format!(
                "{}: language/source/usage is missing or invalid",
                fixture.id
            )
            .into());
        }
        if !matches!(
            fixture.kind.as_str(),
            "speech" | "synthetic_tts" | "silence" | "background"
        ) {
            return Err(format!("{}: invalid fixture kind", fixture.id).into());
        }
        if matches!(fixture.kind.as_str(), "speech" | "synthetic_tts")
            && (fixture.reference.trim().is_empty() || fixture.speech_segments_ms.is_empty())
        {
            return Err(format!(
                "{}: speech reference or segment bounds are empty",
                fixture.id
            )
            .into());
        }
        let audio_path = root.join(&fixture.path);
        if sha256(&audio_path)? != fixture.sha256.to_lowercase() {
            return Err(format!("{}: audio hash mismatch", fixture.id).into());
        }
        let mut reader = hound::WavReader::open(&audio_path)?;
        let spec = reader.spec();
        if spec.channels != 1
            || spec.sample_rate != 16000
            || reader.duration() == 0
            || reader.duration() > 16000 * 120
        {
            return Err(format!("{}: WAV must be mono 16 kHz, 0..120 seconds", fixture.id).into());
        }
        total_samples += reader.duration() as usize;
        if total_samples > 32 * 1024 * 1024 {
            return Err("fixture corpus exceeds the 128 MiB PCM budget".into());
        }
        let pcm: Vec<f32> = match (spec.sample_format, spec.bits_per_sample) {
            (hound::SampleFormat::Int, 16) => reader
                .samples::<i16>()
                .map(|s| s.map(|s| s as f32 / 32768.0))
                .collect::<Result<_, _>>()?,
            (hound::SampleFormat::Float, 32) => {
                reader.samples::<f32>().collect::<Result<_, _>>()?
            }
            _ => return Err("WAV must be PCM16 or float32".into()),
        };
        if pcm.iter().any(|s| !s.is_finite() || s.abs() > 1.0) {
            return Err("WAV samples must be finite and normalized".into());
        }
        let duration_ms = pcm.len() as u64 * 1000 / 16000;
        let mut previous_end = 0;
        for [start, end] in &fixture.speech_segments_ms {
            if start < &previous_end || start >= end || *end > duration_ms {
                return Err(format!("{}: invalid speech segments", fixture.id).into());
            }
            previous_end = *end;
        }
        inputs.push(Input { fixture, pcm });
    }
    Ok(inputs)
}
