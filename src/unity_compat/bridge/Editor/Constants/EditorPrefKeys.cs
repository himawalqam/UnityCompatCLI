namespace UnityCompatVendor.Editor.Constants
{
    /// <summary>
    /// Centralized list of EditorPrefs keys used by the MCP for Unity package.
    /// Keeping them in one place avoids typos and simplifies migrations.
    /// </summary>
    internal static class EditorPrefKeys
    {
        internal const string UseHttpTransport = "UnityCompatVendor.UseHttpTransport";
        internal const string HttpTransportScope = "UnityCompatVendor.HttpTransportScope"; // "local" | "remote"
        internal const string LastLocalHttpServerPid = "UnityCompatVendor.LocalHttpServer.LastPid";
        internal const string LastLocalHttpServerPort = "UnityCompatVendor.LocalHttpServer.LastPort";
        internal const string LastLocalHttpServerStartedUtc = "UnityCompatVendor.LocalHttpServer.LastStartedUtc";
        internal const string LastLocalHttpServerPidArgsHash = "UnityCompatVendor.LocalHttpServer.LastPidArgsHash";
        internal const string LastLocalHttpServerPidFilePath = "UnityCompatVendor.LocalHttpServer.LastPidFilePath";
        internal const string LastLocalHttpServerInstanceToken = "UnityCompatVendor.LocalHttpServer.LastInstanceToken";
        internal const string DebugLogs = "UnityCompatVendor.DebugLogs";
        internal const string ValidationLevel = "UnityCompatVendor.ValidationLevel";
        internal const string UnitySocketPort = "UnityCompatVendor.UnitySocketPort";
        internal const string ResumeHttpAfterReload = "UnityCompatVendor.ResumeHttpAfterReload";
        internal const string ResumeStdioAfterReload = "UnityCompatVendor.ResumeStdioAfterReload";

        internal const string UvxPathOverride = "UnityCompatVendor.UvxPath";
        internal const string ClaudeCliPathOverride = "UnityCompatVendor.ClaudeCliPath";
        internal const string ClientProjectDirOverride = "UnityCompatVendor.ClientProjectDir";

        internal const string HttpBaseUrl = "UnityCompatVendor.HttpUrl";
        internal const string HttpRemoteBaseUrl = "UnityCompatVendor.HttpRemoteUrl";
        internal const string SessionId = "UnityCompatVendor.SessionId";
        internal const string WebSocketUrlOverride = "UnityCompatVendor.WebSocketUrl";
        internal const string GitUrlOverride = "UnityCompatVendor.GitUrlOverride";
        internal const string DevModeForceServerRefresh = "UnityCompatVendor.DevModeForceServerRefresh";
        internal const string ProjectScopedToolsLocalHttp = "UnityCompatVendor.ProjectScopedTools.LocalHttp";
        internal const string AllowLanHttpBind = "UnityCompatVendor.Security.AllowLanHttpBind";
        internal const string AllowInsecureRemoteHttp = "UnityCompatVendor.Security.AllowInsecureRemoteHttp";

        internal const string PackageDeploySourcePath = "UnityCompatVendor.PackageDeploy.SourcePath";
        internal const string PackageDeployLastBackupPath = "UnityCompatVendor.PackageDeploy.LastBackupPath";
        internal const string PackageDeployLastTargetPath = "UnityCompatVendor.PackageDeploy.LastTargetPath";
        internal const string PackageDeployLastSourcePath = "UnityCompatVendor.PackageDeploy.LastSourcePath";

        internal const string ServerSrc = "UnityCompatVendor.ServerSrc";
        internal const string UseEmbeddedServer = "UnityCompatVendor.UseEmbeddedServer";
        internal const string LockCursorConfig = "UnityCompatVendor.LockCursorConfig";
        internal const string AutoRegisterEnabled = "UnityCompatVendor.AutoRegisterEnabled";
        internal const string ToolEnabledPrefix = "UnityCompatVendor.ToolEnabled.";
        internal const string ToolFoldoutStatePrefix = "UnityCompatVendor.ToolFoldout.";
        internal const string ResourceEnabledPrefix = "UnityCompatVendor.ResourceEnabled.";
        internal const string ResourceFoldoutStatePrefix = "UnityCompatVendor.ResourceFoldout.";
        internal const string EditorWindowActivePanel = "UnityCompatVendor.EditorWindow.ActivePanel";
        internal const string LastSelectedClientId = "UnityCompatVendor.LastSelectedClientId";
        internal const string ClientDetailsFoldoutOpen = "UnityCompatVendor.ClientConfig.DetailsFoldoutOpen";

        internal const string SetupCompleted = "UnityCompatVendor.SetupCompleted";
        internal const string SetupDismissed = "UnityCompatVendor.SetupDismissed";

        internal const string CustomToolRegistrationEnabled = "UnityCompatVendor.CustomToolRegistrationEnabled";

        internal const string LastUpdateCheck = "UnityCompatVendor.LastUpdateCheck";
        internal const string LatestKnownVersion = "UnityCompatVendor.LatestKnownVersion";
        internal const string LastAssetStoreUpdateCheck = "UnityCompatVendor.LastAssetStoreUpdateCheck";
        internal const string LatestKnownAssetStoreVersion = "UnityCompatVendor.LatestKnownAssetStoreVersion";
        internal const string LastStdIoUpgradeVersion = "UnityCompatVendor.LastStdIoUpgradeVersion";

        internal const string TelemetryDisabled = "UnityCompatVendor.TelemetryDisabled";
        internal const string CustomerUuid = "UnityCompatVendor.CustomerUUID";

        internal const string ApiKey = "UnityCompatVendor.ApiKey";

        internal const string AutoStartOnLoad = "UnityCompatVendor.AutoStartOnLoad";
        internal const string HttpServerLaunchConfirmed = "UnityCompatVendor.HttpServerLaunchConfirmed";
        internal const string BatchExecuteMaxCommands = "UnityCompatVendor.BatchExecute.MaxCommands";
        internal const string LogRecordEnabled = "UnityCompatVendor.LogRecordEnabled";

        internal const string ExecuteCodeCompiler = "UnityCompatVendor.ExecuteCode.Compiler";

        // AI Asset Generation — NON-SECRET config only. Provider API keys live in the OS
        // secure store (UnityCompatVendor.Editor.Security.SecureKeyStore), never in EditorPrefs.
        internal const string AssetGenSelectedModelProvider = "UnityCompatVendor.AssetGen.ModelProvider";
        internal const string AssetGenSelectedImageProvider = "UnityCompatVendor.AssetGen.ImageProvider";
        internal const string AssetGenDefaultFormat = "UnityCompatVendor.AssetGen.Format";
        internal const string AssetGenOutputRoot = "UnityCompatVendor.AssetGen.OutputRoot";
        internal const string AssetGenAutoNormalize = "UnityCompatVendor.AssetGen.AutoNormalize";
        internal const string AssetGenProviderEnabledPrefix = "UnityCompatVendor.AssetGen.Enabled.";
    }
}
