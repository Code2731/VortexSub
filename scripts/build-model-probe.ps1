param(
    [ValidateSet('cpu', 'cuda')] [string] $Backend = 'cpu',
    [switch] $Offline,
    [ValidateSet('echosub-model-probe', 'echosub-worker')] [string] $Package = 'echosub-model-probe',
    [switch] $Vad
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
    $env:PATH = (Join-Path $env:USERPROFILE '.cargo/bin') + ';' + $env:PATH
}
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) {
    $vsCmake = Join-Path $env:ProgramFiles 'Microsoft Visual Studio/2022/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin'
    if (Test-Path (Join-Path $vsCmake 'cmake.exe')) { $env:PATH = $vsCmake + ';' + $env:PATH }
}
if (-not (Get-Command cmake -ErrorAction SilentlyContinue)) { throw 'CMake is required for the native whisper.cpp probe' }
# Bundled sys bindings contain Linux ABI assertions. Generate bindings for Windows.
Remove-Item Env:WHISPER_DONT_GENERATE_BINDINGS -ErrorAction SilentlyContinue
if (-not $env:LIBCLANG_PATH) {
    $llvmBin = Join-Path $env:ProgramFiles 'LLVM/bin'
    if (Test-Path (Join-Path $llvmBin 'libclang.dll')) { $env:LIBCLANG_PATH = $llvmBin }
}
if (-not $env:LIBCLANG_PATH) { throw 'Set LIBCLANG_PATH to the directory containing libclang.dll' }
$ninjaBin = Join-Path $env:ProgramFiles 'Microsoft Visual Studio/2022/Community/Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja'
if (Test-Path (Join-Path $ninjaBin 'ninja.exe')) { $env:PATH = $ninjaBin + ';' + $env:PATH }
if (-not $env:CMAKE_GENERATOR -and (Get-Command ninja -ErrorAction SilentlyContinue)) { $env:CMAKE_GENERATOR = 'Ninja' }
# The binding/cmake combination replaced Release flags without /O2 on Windows.
if (-not $env:CMAKE_C_FLAGS_RELEASE) { $env:CMAKE_C_FLAGS_RELEASE = '/O2 /DNDEBUG' }
if (-not $env:CMAKE_CXX_FLAGS_RELEASE) { $env:CMAKE_CXX_FLAGS_RELEASE = '/O2 /DNDEBUG /utf-8' }
if ($Backend -eq 'cuda') {
    if (-not $env:CUDA_PATH) { throw 'CUDA_PATH must point to the installed CUDA Toolkit' }
    $env:PATH = (Join-Path $env:CUDA_PATH 'bin') + ';' + $env:PATH
    if (-not (Get-Command ninja -ErrorAction SilentlyContinue)) { throw 'Ninja is required for the Windows CUDA probe build' }
    if (-not $env:CMAKE_GENERATOR) { $env:CMAKE_GENERATOR = 'Ninja' }
    # The binding sets a Unix-only -fPIC flag. Override it for MSVC.
    if (-not (Test-Path Env:CMAKE_CUDA_FLAGS)) { $env:CMAKE_CUDA_FLAGS = ' ' }
    # Avoid nvcc's default sm_52 target; compile for the GPU on this machine.
    if (-not $env:CMAKE_CUDA_ARCHITECTURES) { $env:CMAKE_CUDA_ARCHITECTURES = 'native' }
}
Push-Location $repo
try {
    $targetRoot = if ($env:CARGO_TARGET_DIR) { [IO.Path]::GetFullPath($env:CARGO_TARGET_DIR) } else { Join-Path $repo "target/model-probe-$Backend" }
    if (-not $targetRoot.StartsWith($repo.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Native probe target must be inside this repository' }
    $profile = [ordered]@{backend=$Backend;generator=$env:CMAKE_GENERATOR;c_release=$env:CMAKE_C_FLAGS_RELEASE;cxx_release=$env:CMAKE_CXX_FLAGS_RELEASE;cuda_flags=$env:CMAKE_CUDA_FLAGS;cuda_arch=$env:CMAKE_CUDA_ARCHITECTURES;cuda_path=$env:CUDA_PATH;libclang=$env:LIBCLANG_PATH}
    $profileJson = $profile | ConvertTo-Json -Compress
    $stamp = Join-Path $targetRoot 'echosub-native-profile.json'
    $previous = if (Test-Path -LiteralPath $stamp) { [IO.File]::ReadAllText($stamp) } else { '' }
    # Upstream does not track all CMake environment flags in Cargo's cache.
    if ($previous -ne $profileJson) {
        & cargo clean -p whisper-rs-sys --release --target-dir $targetRoot
        if ($LASTEXITCODE -ne 0) { throw 'Failed to clear the previous native configuration' }
    }
    $arguments = @('build', '-p', $Package, '--release', '--locked', '--features')
    if ($Backend -eq 'cuda') { $arguments += 'cuda' } elseif ($Package -eq 'echosub-worker') { $arguments += 'native-asr' } else { $arguments += 'native' }
    if ($Vad) {
        if ($Package -ne 'echosub-worker') { throw '-Vad requires echosub-worker package' }
        $arguments[$arguments.Count - 1] += ',native-vad'
    }
    if ($Offline -or $env:ECHOSUB_OFFLINE -eq '1') { $arguments += '--offline' }
    $arguments += @('--target-dir', $targetRoot)
    & cargo @arguments
    if ($LASTEXITCODE -ne 0) { throw 'Native model probe build failed' }
    [IO.File]::WriteAllText($stamp, $profileJson)
}
finally { Pop-Location }
