use crate::{Backend, Error, CONTEXT, FRAME, STATE};
use ort::{session::Session, value::Tensor};
use sha2::{Digest, Sha256};
use std::{io::Read, path::Path};

pub struct OnnxBackend {
    session: Session,
}
pub fn verify(path: &Path, hash: &str) -> Result<(), Error> {
    if !path.is_absolute() || hash.len() != 64 || !hash.bytes().all(|b| b.is_ascii_hexdigit()) {
        return Err(Error::Model);
    }
    let mut file = std::fs::File::open(path).map_err(|_| Error::Model)?;
    let mut digest = Sha256::new();
    let mut buffer = [0u8; 65536];
    loop {
        let n = file.read(&mut buffer).map_err(|_| Error::Model)?;
        if n == 0 {
            break;
        }
        digest.update(&buffer[..n]);
    }
    if format!("{:x}", digest.finalize()) != hash.to_ascii_lowercase() {
        return Err(Error::Model);
    }
    Ok(())
}
impl OnnxBackend {
    pub fn load(
        model: &Path,
        model_hash: &str,
        runtime: &Path,
        runtime_hash: &str,
    ) -> Result<Self, Error> {
        verify(model, model_hash)?;
        verify(runtime, runtime_hash).map_err(|_| Error::Runtime)?;
        ort::init_from(runtime.to_string_lossy())
            .commit()
            .map_err(|_| Error::Runtime)?;
        let session = Session::builder()
            .map_err(|_| Error::Runtime)?
            .with_intra_threads(1)
            .map_err(|_| Error::Runtime)?
            .with_inter_threads(1)
            .map_err(|_| Error::Runtime)?
            .commit_from_file(model)
            .map_err(|_| Error::Model)?;
        if session.inputs.len() != 3
            || session.outputs.len() != 2
            || !["input", "state", "sr"]
                .iter()
                .all(|name| session.inputs.iter().any(|x| x.name == *name))
            || !["output", "stateN"]
                .iter()
                .all(|name| session.outputs.iter().any(|x| x.name == *name))
        {
            return Err(Error::Model);
        }
        Ok(Self { session })
    }
}
impl Backend for OnnxBackend {
    fn infer(
        &mut self,
        input: &[f32; FRAME + CONTEXT],
        state: &[f32; STATE],
    ) -> Result<(f32, [f32; STATE]), Error> {
        let audio = Tensor::from_array(([1, 576], input.to_vec())).map_err(|_| Error::Inference)?;
        let memory =
            Tensor::from_array(([2, 1, 128], state.to_vec())).map_err(|_| Error::Inference)?;
        let rate = Tensor::from_array((Vec::<usize>::new(), vec![16000i64]))
            .map_err(|_| Error::Inference)?;
        let outputs = self
            .session
            .run(ort::inputs! {"input"=>audio,"state"=>memory,"sr"=>rate})
            .map_err(|_| Error::Inference)?;
        let (shape, p) = outputs["output"]
            .try_extract_tensor::<f32>()
            .map_err(|_| Error::InvalidOutput)?;
        let (state_shape, next) = outputs["stateN"]
            .try_extract_tensor::<f32>()
            .map_err(|_| Error::InvalidOutput)?;
        if &shape[..] != [1, 1]
            || &state_shape[..] != [2, 1, 128]
            || p.len() != 1
            || next.len() != 256
        {
            return Err(Error::InvalidOutput);
        }
        let mut s = [0.; STATE];
        s.copy_from_slice(next);
        Ok((p[0], s))
    }
}
