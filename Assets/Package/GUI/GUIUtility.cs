﻿using UnityEditor;
using UnityEngine;

namespace GitRepositoryManager
{
    public static class GUIUtility
    {
        public static string GetLoadingDots()
        {
            string dots = string.Empty;
            int dotCount = Mathf.FloorToInt((float)(EditorApplication.timeSinceStartup % 3)) + 1;
            for (int i = 0; i < dotCount; i++) { dots += "."; }
            return dots;
        }

        public static void DrawLine()
        {
            Rect lineRect = EditorGUILayout.GetControlRect();
            lineRect.y += lineRect.height / 2f;
            lineRect.height = 1;
            GUI.Box(lineRect, "");
        }
    }
}
