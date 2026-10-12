"""本机 CC Switch 接入：保留官方登录，凭据只通过 DPAPI 命令管道提供。"""
import ctypes
import hashlib
import json
import os
from pathlib import Path
import re
import sqlite3
import tomllib
from datetime import datetime, timezone, timedelta

ROOT = Path(__file__).resolve().parent.parent
HOME = Path.home()
CC = HOME / ".cc-switch"
CODEX = Path(os.environ.get("CODEX_HOME", str(HOME / ".codex")))
HELPER = ROOT / ".local/release/Daemon/CodexPool.Daemon.exe"
PROVIDER_ID = "codex-pool-local-v2"
MODEL_PROVIDER_ID = "codex_pool_local"
PROVIDER_NAME = "Codex Pool（本地代理，保留登录）"


class Blob(ctypes.Structure):
    _fields_ = [("size", ctypes.c_uint32), ("data", ctypes.POINTER(ctypes.c_ubyte))]


def encrypted_backup(path, payload):
    buffer = ctypes.create_string_buffer(payload)
    source = Blob(len(payload), ctypes.cast(buffer, ctypes.POINTER(ctypes.c_ubyte)))
    target = Blob()
    protect = ctypes.windll.crypt32.CryptProtectData
    protect.argtypes = [ctypes.POINTER(Blob), ctypes.c_wchar_p, ctypes.c_void_p,
                        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint32, ctypes.POINTER(Blob)]
    protect.restype = ctypes.c_int
    if not protect(ctypes.byref(source), "Codex Pool 配置备份", None, None, None, 1, ctypes.byref(target)):
        raise RuntimeError("DPAPI backup failed")
    try:
        path.write_bytes(ctypes.string_at(target.data, target.size))
    finally:
        free = ctypes.windll.kernel32.LocalFree
        free.argtypes = [ctypes.c_void_p]
        free.restype = ctypes.c_void_p
        free(target.data)
        ctypes.memset(buffer, 0, len(payload))


def atomic_write(path, payload):
    temp = path.with_name(path.name + ".codex-pool.tmp")
    with temp.open("xb") as file:
        file.write(payload)
        file.flush()
        os.fsync(file.fileno())
    os.replace(temp, path)


def main():
    if os.name != "nt" or not HELPER.is_file():
        raise RuntimeError("Build the Windows credential helper first")
    config_path = CODEX / "config.toml"
    settings_path = CC / "settings.json"
    original_config = config_path.read_bytes()
    original_settings = settings_path.read_bytes()
    config_text = original_config.decode("utf-8-sig")
    settings = json.loads(original_settings)
    parsed = tomllib.loads(config_text)
    auth_path = CODEX / "auth.json"
    auth_hash = hashlib.sha256(auth_path.read_bytes()).digest()
    connection = sqlite3.connect(CC / "cc-switch.db", timeout=10)
    existing = connection.execute("SELECT settings_config FROM providers WHERE app_type=? AND id=?", ("codex", PROVIDER_ID)).fetchone()
    if existing:
        raise RuntimeError("Codex Pool provider already exists; no existing row overwritten")
    if MODEL_PROVIDER_ID in parsed.get("model_providers", {}):
        raise RuntimeError("Provider table already exists; no existing table overwritten")
    proxy = connection.execute("SELECT live_takeover_active FROM proxy_config WHERE app_type=?", ("codex",)).fetchone()
    if proxy and proxy[0]:
        raise RuntimeError("CC Switch proxy takeover is active")
    backup = Path(os.environ["LOCALAPPDATA"]) / "CodexPool" / "cc-switch-backups" / datetime.now(timezone(timedelta(hours=8))).strftime("%Y%m%d-%H%M%S")
    backup.mkdir(parents=True, exist_ok=False)
    memory = sqlite3.connect(":memory:")
    connection.backup(memory)
    encrypted_backup(backup / "cc-switch.db.dpapi", memory.serialize())
    memory.close()
    encrypted_backup(backup / "config.toml.dpapi", original_config)
    encrypted_backup(backup / "settings.json.dpapi", original_settings)
    # Change only a root setting, preserving all other tables/preferences verbatim.
    table = re.search(r"(?m)^\s*\[", config_text)
    boundary = table.start() if table else len(config_text)
    root, tables = config_text[:boundary], config_text[boundary:]
    line = f'model_provider = "{MODEL_PROVIDER_ID}"'
    if re.search(r"(?m)^\s*model_provider\s*=", root):
        root = re.sub(r"(?m)^\s*model_provider\s*=.*$", line, root)
    else:
        root = line + "\n" + root
    # Provider routing must not select a cheaper model or override the user's model choice.
    config_text = root + tables + "\n\n" + f'''[model_providers.{MODEL_PROVIDER_ID}]
name = "Codex Pool Local"
base_url = "http://127.0.0.1:3210/v1"
wire_api = "responses"
requires_openai_auth = false
supports_websockets = false

[model_providers.{MODEL_PROVIDER_ID}.auth]
command = {json.dumps(HELPER.as_posix())}
args = ["model-credential"]
timeout_ms = 5000
refresh_interval_ms = 300000
'''
    tomllib.loads(config_text)
    settings["preserveCodexOfficialAuthOnSwitch"] = True
    settings["currentProviderCodex"] = PROVIDER_ID
    provider = json.dumps({"auth": {}, "config": config_text}, ensure_ascii=False)
    try:
        connection.execute("BEGIN IMMEDIATE")
        connection.execute("UPDATE providers SET is_current=0 WHERE app_type=?", ("codex",))
        connection.execute("INSERT INTO providers(id,app_type,name,settings_config,category,created_at,notes,meta,is_current,in_failover_queue) VALUES(?,?,?,?,?,?,?,?,?,?)",
                           (PROVIDER_ID, "codex", PROVIDER_NAME, provider, "custom", int(datetime.now().timestamp() * 1000),
                            "模型走 localhost:3210；保留官方登录；A/B 调度与额度由 Codex Pool 管理。", "{}", 1, 0))
        atomic_write(config_path, config_text.encode("utf-8"))
        atomic_write(settings_path, json.dumps(settings, ensure_ascii=False, indent=2).encode("utf-8"))
        assert hashlib.sha256(auth_path.read_bytes()).digest() == auth_hash
        connection.commit()
    except Exception:
        connection.rollback()
        atomic_write(config_path, original_config)
        atomic_write(settings_path, original_settings)
        raise
    finally:
        connection.close()
    print(json.dumps({"provider": PROVIDER_NAME, "activated": True,
                      "authJsonUnchanged": True, "credentialStorage": "DPAPI command pipe",
                      "baseUrl": "http://127.0.0.1:3210/v1"}, ensure_ascii=False))


if __name__ == "__main__":
    try:
        main()
    except Exception:
        # Exceptions can include original configuration text; never print their details.
        print("配置未完成，现有登录没有被写入或覆盖。请检查已存在的提供商、接管模式和备份。")
        raise SystemExit(1)
