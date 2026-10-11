using System;

namespace UnityCompat.Editor
{
    /// <summary>Extend the CLI with a public static object/Task HandleCommand(JObject) method.</summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
    public sealed class CliCommandAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public string Group { get; set; } = "custom";
        public bool MainThreadRequired { get; set; } = true;
        public bool RuntimeOnly { get; set; }
        public string[] Tags { get; set; } = new string[0];
        public CliCommandAttribute(string name, string description)
        { Name = name; Description = description; }
    }

    [AttributeUsage(AttributeTargets.Parameter)]
    public sealed class CliArgAttribute : Attribute
    {
        public string Name { get; }
        public string Description { get; }
        public bool Required { get; set; }
        public object DefaultValue { get; set; }
        public CliArgAttribute(string name, string description = "") { Name = name; Description = description; }
    }
}
