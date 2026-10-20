"""Locate the NerfStudio installation (it lives in its own Python 3.10 env)."""
import json
import os
import shutil
import subprocess
from pathlib import Path

from . import settings as settings_mod

EXE = ".exe" if os.name == "nt" else ""


def _scripts_dir(env_root: Path) -> Path:
    return env_root / "Scripts" if os.name == "nt" else env_root / "bin"


def _candidate_bin_dirs(cfg: dict):
    if cfg.get("nerfstudio_bin"):
        yield Path(cfg["nerfstudio_bin"])
    if os.environ.get("NERFSTUDIO_BIN"):
        yield Path(os.environ["NERFSTUDIO_BIN"])
    on_path = shutil.which("ns-train")
    if on_path:
        yield Path(on_path).parent

    env = cfg.get("conda_env") or "nerfstudio"
    home = Path.home()
    bases = [home, Path(os.environ.get("LOCALAPPDATA", home)), Path(os.environ.get("PROGRAMDATA", "C:/ProgramData"))]
    for base in bases:
        for dist in ("miniconda3", "anaconda3", "miniforge3", "mambaforge", "Miniconda3", "Anaconda3"):
            yield _scripts_dir(base / dist / "envs" / env)
    yield _scripts_dir(home / ".conda" / "envs" / env)

    conda = shutil.which("conda")
    if conda:
        try:
            out = subprocess.run([conda, "env", "list", "--json"], capture_output=True, text=True, timeout=20)
            for p in json.loads(out.stdout).get("envs", []):
                if Path(p).name == env:
                    yield _scripts_dir(Path(p))
        except (OSError, ValueError, subprocess.SubprocessError):
            pass


_cache = {}


def find(refresh: bool = False):
    """Return dict(bin_dir, env_root) or None if ns-train can't be found."""
    if not refresh and "result" in _cache:
        return _cache["result"]
    result = None
    for d in _candidate_bin_dirs(settings_mod.load()):
        if (d / f"ns-train{EXE}").exists():
            env_root = d.parent if d.name.lower() in ("scripts", "bin") else d
            result = {"bin_dir": d, "env_root": env_root}
            break
    _cache["result"] = result
    return result


def tool(name: str) -> str:
    env = find()
    if not env:
        raise RuntimeError("NerfStudio not found. Run setup_nerfstudio.ps1 or set nerfstudio_bin in settings.json.")
    return str(env["bin_dir"] / f"{name}{EXE}")


def _cuda_version():
    """(major, minor) of the CUDA toolkit nvcc will use, or None."""
    cuda = os.environ.get("CUDA_PATH") or ""
    name = Path(cuda).name.lower().lstrip("v")
    try:
        major, minor = name.split(".")[:2]
        return int(major), int(minor)
    except ValueError:
        return None


def _msvc_toolset():
    """(vcvars64.bat, toolset version) for the newest MSVC that the CUDA toolkit accepts.

    gsplat (splatfacto) JIT-compiles its CUDA kernels on first use, which needs cl.exe.
    CUDA < 12.4 refuses MSVC 14.40+ (VS 2022 17.10 and later), so prefer an older toolset there."""
    vswhere = Path(os.environ.get("ProgramFiles(x86)", r"C:\Program Files (x86)")) / \
        "Microsoft Visual Studio" / "Installer" / "vswhere.exe"
    if not vswhere.exists():
        return None
    try:
        out = subprocess.run([str(vswhere), "-all", "-products", "*", "-format", "json"],
                             capture_output=True, text=True, timeout=30)
        installs = [Path(i["installationPath"]) for i in json.loads(out.stdout or "[]")]
    except (OSError, ValueError, KeyError, subprocess.SubprocessError):
        return None
    cuda = _cuda_version()
    limit = (14, 40) if cuda and cuda < (12, 4) else (99, 0)
    best = None
    for root in installs:
        vcvars = root / "VC" / "Auxiliary" / "Build" / "vcvars64.bat"
        tools = root / "VC" / "Tools" / "MSVC"
        if not vcvars.exists() or not tools.exists():
            continue
        for d in tools.iterdir():
            try:
                ver = tuple(int(p) for p in d.name.split("."))
            except ValueError:
                continue
            if ver[:2] < limit and (d / "bin" / "Hostx64" / "x64" / "cl.exe").exists():
                if best is None or ver > best[1]:
                    best = (vcvars, ver)
    return best and (best[0], ".".join(str(p) for p in best[1][:2]))


def msvc_env():
    """Environment variables from vcvars64.bat (cached), or {} if no usable compiler."""
    if "msvc" in _cache:
        return _cache["msvc"]
    result = {}
    if os.name == "nt" and not shutil.which("cl"):
        found = _msvc_toolset()
        if found:
            vcvars, ver = found
            try:
                out = subprocess.run(f'"{vcvars}" -vcvars_ver={ver} >nul && set', shell=True,
                                     capture_output=True, text=True, timeout=120)
                for line in out.stdout.splitlines():
                    k, sep, v = line.partition("=")
                    if sep and k:
                        result[k] = v
                if result and not any(k.upper() == "VCTOOLSINSTALLDIR" for k in result):
                    result = {}  # vcvars failed
            except (OSError, subprocess.SubprocessError):
                result = {}
            if result:
                result["DISTUTILS_USE_SDK"] = "1"
    _cache["msvc"] = result
    return result


def subprocess_env() -> dict:
    """Environment for ns-* processes: env DLL dirs on PATH, unbuffered, wide plain console,
    plus the MSVC compiler so gsplat can build its CUDA kernels."""
    env = dict(os.environ)
    msvc = msvc_env()
    if msvc:
        env = {k: v for k, v in env.items() if k.upper() not in {m.upper() for m in msvc}}
        env.update(msvc)
    found = find()
    if found:
        root = found["env_root"]
        extra = [root, root / "Library" / "bin", root / "Library" / "usr" / "bin", found["bin_dir"]]
        env["PATH"] = os.pathsep.join([str(p) for p in extra] + [env.get("PATH", "")])
    env.update({
        "PYTHONUNBUFFERED": "1",
        "PYTHONIOENCODING": "utf-8",
        "PYTHONUTF8": "1",
        "COLUMNS": "220",       # keep rich from wrapping the progress table
        "TERM": "dumb",
        "NO_COLOR": "1",
    })
    return env
