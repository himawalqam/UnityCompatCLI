// Place this file in your project's Assets/Editor/ directory.
using Newtonsoft.Json.Linq;
using UnityCompat.Editor;
using UnityEngine;

[CliCommand("validate_level", "Check that the loaded scene has a camera and at least one collider", Group = "project")]
public static class ValidateLevel
{
    public static object HandleCommand(JObject parameters)
    {
        var cameras = Object.FindObjectsOfType<Camera>();
        var colliders = Object.FindObjectsOfType<Collider>();
        return new { success = cameras.Length > 0 && colliders.Length > 0,
            cameras = cameras.Length, colliders = colliders.Length };
    }
}
