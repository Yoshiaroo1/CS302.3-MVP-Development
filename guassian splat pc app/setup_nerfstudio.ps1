# One-time install of NerfStudio into a conda env called "nerfstudio".
# NerfStudio needs Python 3.10 + CUDA PyTorch, so it gets its own env separate from the app.
# Run from PowerShell:   powershell -ExecutionPolicy Bypass -File .\setup_nerfstudio.ps1
$ErrorActionPreference = "Stop"
$EnvName = "nerfstudio"

function Find-Conda {
    $cmd = Get-Command conda -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
        foreach ($base in @("C:\cs302.3", $env:USERPROFILE, $env:LOCALAPPDATA, $env:ProgramData)) {
        foreach ($dist in @("miniconda3", "anaconda3", "miniforge3")) {
            $p = Join-Path $base "$dist\Scripts\conda.exe"
            if (Test-Path $p) { return $p }
        }
    }
    return $null
}

$conda = Find-Conda
if (-not $conda) {
    Write-Host "Conda not found - installing Miniconda (per-user) with winget..." -ForegroundColor Cyan
    winget install --id Anaconda.Miniconda3 -e --scope user --accept-package-agreements --accept-source-agreements
    $conda = Find-Conda
    if (-not $conda) { throw "Miniconda install finished but conda.exe wasn't found. Open a new terminal and re-run this script." }
}
Write-Host "Using conda: $conda" -ForegroundColor Green

$envs = & $conda env list --json | ConvertFrom-Json
$envPath = $envs.envs | Where-Object { (Split-Path $_ -Leaf) -eq $EnvName } | Select-Object -First 1
if (-not $envPath) {
    Write-Host "Creating conda env '$EnvName' (Python 3.10)..." -ForegroundColor Cyan
    & $conda create -n $EnvName python=3.10 -y --override-channels -c conda-forge
    $envs = & $conda env list --json | ConvertFrom-Json
    $envPath = $envs.envs | Where-Object { (Split-Path $_ -Leaf) -eq $EnvName } | Select-Object -First 1
}
$py = Join-Path $envPath "python.exe"

Write-Host "Installing CUDA PyTorch (cu118)..." -ForegroundColor Cyan
& $py -m pip install --upgrade pip "setuptools<70"
& $py -m pip install torch==2.1.2+cu118 torchvision==0.16.2+cu118 --extra-index-url https://download.pytorch.org/whl/cu118

Write-Host "Installing NerfStudio..." -ForegroundColor Cyan
& $py -m pip install nerfstudio "numpy<2"

# splatfacto's gsplat otherwise JIT-compiles its CUDA kernels on first use, which fails on Windows
# with CUDA 11.8 (a cub header clashes with the 'small' macro from rpcndr.h). Use the official
# prebuilt wheel for torch 2.1 + cu118 + Python 3.10 instead.
Write-Host "Installing prebuilt gsplat (splatfacto)..." -ForegroundColor Cyan
& $py -m pip install --force-reinstall --no-deps "https://github.com/nerfstudio-project/gsplat/releases/download/v1.4.0/gsplat-1.4.0%2Bpt21cu118-cp310-cp310-win_amd64.whl"

# COLMAP's Python bindings: bundle-adjusts the phone poses and lens during dataset preparation.
Write-Host "Installing pycolmap (pose refinement)..." -ForegroundColor Cyan
& $py -m pip install --no-deps "pycolmap==4.2.1"

# tiny-cuda-nn makes nerfacto ~3-5x faster but needs the CUDA 11.8 toolkit + VS 2022 C++ build tools.
# NerfStudio falls back to a slower pure-PyTorch implementation if it's missing.
if (Get-Command nvcc -ErrorAction SilentlyContinue) {
    Write-Host "CUDA toolkit found - building tiny-cuda-nn (optional, can take 10+ minutes)..." -ForegroundColor Cyan
    & $py -m pip install ninja wheel
    & $py -m pip install --no-build-isolation git+https://github.com/NVlabs/tiny-cuda-nn/#subdirectory=bindings/torch
    if ($LASTEXITCODE -ne 0) { Write-Warning "tiny-cuda-nn build failed - training will still work, just slower." }
} else {
    Write-Host "Skipping tiny-cuda-nn (no CUDA toolkit). Training works, just slower." -ForegroundColor Yellow
}

& $py -c "import torch, nerfstudio; from gsplat.cuda._backend import _C; import pycolmap; print('torch', torch.__version__, 'CUDA', torch.cuda.is_available(), 'gsplat kernels', _C is not None, 'pycolmap', pycolmap.__version__)"
Write-Host "`nDone. NerfStudio is in: $envPath" -ForegroundColor Green
Write-Host "Start the app with run.bat - it will find this env automatically."
