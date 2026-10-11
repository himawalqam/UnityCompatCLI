using UnityCompatVendor.Editor.Constants;
using UnityCompatVendor.Editor.Setup;
using UnityCompatVendor.Editor.Windows;
using UnityEditor;
using UnityEngine;

namespace UnityCompatVendor.Editor.MenuItems
{
    public static class UnityCompatVendorMenu
    {
        /* UnityCompat: upstream menus disabled */
        public static void ToggleMCPWindow()
        {
            UnityCompatVendorEditorWindow.ShowWindow();
        }

        /* UnityCompat: upstream menus disabled */
        public static void ShowSetupWindow()
        {
            SetupWindowService.ShowSetupWindow();
        }


        /* UnityCompat: upstream menus disabled */
        public static void ShowEditorPrefsWindow()
        {
            EditorPrefsWindow.ShowWindow();
        }
    }
}
