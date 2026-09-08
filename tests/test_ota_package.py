"""Exercise the production PowerShell packager with an ephemeral test key."""
import base64
import hashlib
import json
from pathlib import Path
import subprocess
import tempfile

root = Path(__file__).resolve().parents[1]
with tempfile.TemporaryDirectory(prefix="intercom-ota-test-") as directory:
    work = Path(directory)
    image = work / "test.bin"
    image.write_bytes(bytes(range(256)) * 16)
    key, public = work / "test-key.pem", work / "test-public.pem"
    subprocess.run(["openssl", "ecparam", "-name", "prime256v1", "-genkey", "-noout", "-out", str(key)], check=True)
    subprocess.run(["openssl", "pkey", "-in", str(key), "-pubout", "-out", str(public)], check=True)
    subprocess.run(["pwsh", "-NoProfile", "-File", str(root / "tools/ota/Create-OtaPackage.ps1"),
                    "-Image", str(image), "-Version", "test-p3", "-PrivateKey", str(key)], check=True)
    manifest = json.loads(Path(str(image) + ".ota.json").read_text())
    assert manifest["protocol"] == 3
    assert manifest["size"] == image.stat().st_size
    assert manifest["sha256"] == hashlib.sha256(image.read_bytes()).hexdigest()
    canonical = work / "canonical.txt"
    canonical.write_text(f"wifi-intercom-ota-1\n{manifest['version']}\n{manifest['protocol']}\n{manifest['size']}\n{manifest['sha256']}\n")
    signature = work / "signature.der"
    signature.write_bytes(base64.b64decode(manifest["signature"]))
    command = ["openssl", "dgst", "-sha256", "-verify", str(public), "-signature", str(signature), str(canonical)]
    subprocess.run(command, check=True)
    canonical.write_text(canonical.read_text().replace("test-p3", "tampered"))
    assert subprocess.run(command, capture_output=True).returncode != 0
print("PASS OTA packager defaults to p3; SHA-256 and ECDSA verify; tampering rejected")
