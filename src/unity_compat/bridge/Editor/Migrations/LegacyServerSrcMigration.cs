using System;
using UnityCompatVendor.Editor.Constants;
using UnityCompatVendor.Editor.Helpers;
using UnityCompatVendor.Editor.Services;
using UnityEditor;
using UnityEngine;

namespace UnityCompatVendor.Editor.Migrations
{
    /// <summary>
    /// Detects legacy embedded-server preferences and migrates configs to the new uvx/stdio path once.
    /// </summary>
    /* UnityCompat: upstream auto-start disabled */
    internal static class LegacyServerSrcMigration
    {
        private const string ServerSrcKey = EditorPrefKeys.ServerSrc;
        private const string UseEmbeddedKey = EditorPrefKeys.UseEmbeddedServer;

        static LegacyServerSrcMigration()
        {
            if (Application.isBatchMode)
                return;

            EditorApplication.delayCall += RunMigrationIfNeeded;
        }

        private static void RunMigrationIfNeeded()
        {
            EditorApplication.delayCall -= RunMigrationIfNeeded;

            bool hasServerSrc = EditorPrefs.HasKey(ServerSrcKey);
            bool hasUseEmbedded = EditorPrefs.HasKey(UseEmbeddedKey);

            if (!hasServerSrc && !hasUseEmbedded)
            {
                return;
            }

            try
            {
                McpLog.Info("Detected legacy embedded MCP server configuration. Updating all client configs...");

                var summary = MCPServiceLocator.Client.ConfigureAllDetectedClients();

                if (summary.FailureCount > 0)
                {
                    McpLog.Warn($"Legacy configuration migration finished with errors ({summary.GetSummaryMessage()}). details:");
                    if (summary.Messages != null)
                    {
                        foreach (var message in summary.Messages)
                        {
                            McpLog.Warn($"  {message}");
                        }
                    }
                    McpLog.Warn("Legacy keys will be removed to prevent migration loop. Please configure failing clients manually.");
                }
                else
                {
                    McpLog.Info($"Legacy configuration migration complete ({summary.GetSummaryMessage()})");
                }

                if (hasServerSrc)
                {
                    EditorPrefs.DeleteKey(ServerSrcKey);
                    McpLog.Info("  ✓ Removed legacy key: UnityCompatVendor.ServerSrc");
                }

                if (hasUseEmbedded)
                {
                    EditorPrefs.DeleteKey(UseEmbeddedKey);
                    McpLog.Info("  ✓ Removed legacy key: UnityCompatVendor.UseEmbeddedServer");
                }
            }
            catch (Exception ex)
            {
                McpLog.Error($"Legacy MCP server migration failed: {ex.Message}");
            }
        }
    }
}
