using UnityEditor;
using UnityEngine;

[CustomPropertyDrawer(typeof(DlcReviewScreenshotPathAttribute))]
public sealed class DlcReviewScreenshotPathDrawer : PropertyDrawer
{
    public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
    {
        EditorGUI.BeginProperty(position, label, property);
        var field = position;
        field.width -= 68;
        property.stringValue = EditorGUI.TextField(field, label, property.stringValue);
        var button = new Rect(position.xMax - 63, position.y, 63, position.height);
        if (GUI.Button(button, "Browse"))
        {
            string path = EditorUtility.OpenFilePanelWithFilters("Apple review screenshot", "", new[] { "PNG or JPEG", "png,jpg,jpeg" });
            if (!string.IsNullOrEmpty(path))
            {
                string relative = FileUtil.GetProjectRelativePath(path);
                property.stringValue = string.IsNullOrEmpty(relative) ? path : relative;
            }
        }
        EditorGUI.EndProperty();
    }
}
