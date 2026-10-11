"""Validate failure exit codes and interrupted jobs in the disposable validation project."""
import json
import subprocess
import sys
import time
from pathlib import Path
from unity_compat.protocol import Mailbox

root = Path(__file__).resolve().parents[1]
project = root / "validation/Unity2022"
mailbox = Mailbox(project)
fixture = project / "Assets/Tests/Editor/FixtureTests.cs"
extension = project / "Assets/Editor/InterruptionFixture.cs"
original = fixture.read_text(encoding="utf-8")
checks = []

def refresh():
    old_session = mailbox.status()["session"]
    job = mailbox.submit("refresh_unity", {"mode": "force"}, 30)
    mailbox.wait(job, 30)
    until = time.monotonic() + 45
    while time.monotonic() < until:
        try:
            status = mailbox.status(require_ready=True)
            if status["session"] != old_session: return
        except Exception: pass
        time.sleep(.2)
    raise RuntimeError("Editor did not reload after fixture source change")

try:
    fixture.write_text(original.replace('Assert.That(2 + 2, Is.EqualTo(4));', 'Assert.Fail("Deliberate lifecycle fixture failure");'), encoding="utf-8")
    extension.write_text('''using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using UnityCompat.Editor;
[CliCommand("fixture_interrupt", "Reload while an asynchronous command is running")]
public static class InterruptionFixture {
    public static async Task<object> HandleCommand(JObject p) {
        UnityEditor.EditorApplication.delayCall += () => UnityEditor.EditorUtility.RequestScriptReload();
        await Task.Delay(5000);
        return new {success=true};
    }
}
''', encoding="utf-8")
    refresh()
    child = subprocess.run([sys.executable, "-m", "unity_compat", "test", "--mode", "EditMode",
        "--project", str(project), "--json", "--timeout", "90"], text=True, encoding="utf-8", capture_output=True, timeout=100)
    result = json.loads(child.stdout)
    assert child.returncode == 6 and result["success"] is False, child.stdout + child.stderr
    checks.append({"check": "failing EditMode suite exits 6", "passed": True})
    job = mailbox.submit("fixture_interrupt", {}, 30)
    response = mailbox.wait(job, 30)
    assert response["success"] is False and response["error"]["code"] == "INTERRUPTED", response
    checks.append({"check": "domain reload returns INTERRUPTED without replay", "passed": True})
    # Wait for the new domain before restoring the source.
    until = time.monotonic() + 30
    while time.monotonic() < until:
        try:
            mailbox.status(require_ready=True)
            break
        except Exception: time.sleep(.2)
finally:
    fixture.write_text(original, encoding="utf-8")
    extension.unlink(missing_ok=True)
    refresh()

(root / "docs/lifecycle-validation.json").write_text(json.dumps(checks, indent=2), encoding="utf-8")
print(json.dumps(checks))
