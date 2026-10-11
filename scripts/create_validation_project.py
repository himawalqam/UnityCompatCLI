"""Create a disposable project outside the user's game. Never runs against Beach-Tank."""
import json
from pathlib import Path

root = Path(__file__).resolve().parents[1]
project = root / "validation/Unity2022"
for folder in ("Assets/Editor", "Assets/Tests/Editor", "Packages", "ProjectSettings"):
    (project / folder).mkdir(parents=True, exist_ok=True)
(project / "ProjectSettings/ProjectVersion.txt").write_text("m_EditorVersion: 2022.3.62f3c1\n", encoding="utf-8")
(project / "Packages/manifest.json").write_text(json.dumps({"dependencies": {
    "com.unity.test-framework": "1.1.33", "com.unity.nuget.newtonsoft-json": "3.0.2",
    "com.unity.modules.animation": "1.0.0", "com.unity.modules.audio": "1.0.0", "com.unity.modules.ai": "1.0.0",
    "com.unity.modules.physics": "1.0.0", "com.unity.modules.physics2d": "1.0.0", "com.unity.modules.terrain": "1.0.0",
    "com.unity.modules.terrainphysics": "1.0.0", "com.unity.modules.imageconversion": "1.0.0",
    "com.unity.modules.uielements": "1.0.0", "com.unity.modules.imgui": "1.0.0",
    "com.unity.modules.particlesystem": "1.0.0", "com.unity.modules.unitywebrequest": "1.0.0",
    "com.unity.modules.screencapture": "1.0.0", "com.unity.modules.assetbundle": "1.0.0",
    "com.unity.modules.director": "1.0.0", "com.unity.modules.jsonserialize": "1.0.0"
}}, indent=2), encoding="utf-8")
(project / "Assets/Editor/ExampleCommand.cs").write_text('''using Newtonsoft.Json.Linq;
using UnityCompat.Editor;
[CliCommand("fixture_echo", "Integration fixture for extension discovery")]
public static class FixtureEcho {
    public static object HandleCommand(JObject p) { return new { success = true, echoed = p }; }
}
''', encoding="utf-8")
(project / "Assets/Tests/Editor/CompatFixture.asmdef").write_text(json.dumps({
    "name": "CompatFixture.Tests", "includePlatforms": ["Editor"], "optionalUnityReferences": ["TestAssemblies"]
}), encoding="utf-8")
(project / "Assets/Tests/Editor/FixtureTests.cs").write_text('''using NUnit.Framework;
public class CompatFixtureTests {
    [Test] public void FixturePasses() { Assert.That(2 + 2, Is.EqualTo(4)); }
}
''', encoding="utf-8")
print(project)
