#!/usr/bin/env bash
set -euo pipefail

repo="$(cd "$(dirname "$0")/.." && pwd)"
export AVALONIA_TELEMETRY_OPTOUT=1
export NUGET_PACKAGES="$repo/.nuget/packages"
cd "$repo"

cargo_extra=()
if [[ "${ECHOSUB_OFFLINE:-}" == "1" ]]; then cargo_extra+=(--offline); fi
cargo fmt --all -- --check
cargo test --workspace --locked "${cargo_extra[@]}"
cargo build --workspace --locked "${cargo_extra[@]}"

restore_extra=()
if [[ -n "${ECHOSUB_NUGET_SOURCE:-}" ]]; then restore_extra+=(--source "$ECHOSUB_NUGET_SOURCE"); fi
dotnet restore apps/EchoSub.Desktop/EchoSub.Desktop.csproj --locked-mode "${restore_extra[@]}"
dotnet build apps/EchoSub.Desktop/EchoSub.Desktop.csproj --no-restore
dotnet build tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj
dotnet build benchmarks/EchoSub.TranslationProbe/EchoSub.TranslationProbe.csproj
dotnet run --project tests/EchoSub.ProtocolSmoke/EchoSub.ProtocolSmoke.csproj --no-build -- "$repo/target/debug/echosub-worker"

