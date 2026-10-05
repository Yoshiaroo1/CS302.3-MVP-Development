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


def subprocess_env() -> dict:
    """Environment for ns-* processes: env DLL dirs on PATH, unbuffered, wide plain console."""
    env = dict(os.environ)
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
