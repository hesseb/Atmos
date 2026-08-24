using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

[CustomEditor(typeof(CountryData))]
public class CountryDataEditor : Editor
{

	public override void OnInspectorGUI()
	{
		base.OnInspectorGUI();
		CountryData countryData = target as CountryData;

		if (GUILayout.Button("Load Data"))
		{
			Undo.RecordObject(countryData, "Load Country Data");
			countryData.Load();

			// Undo.RecordObject alone does not mark an asset dirty for serialization - that is
			// what SetDirty is for, and a ScriptableObject asset needs it explicitly to be
			// written back to disk on the next save.
			EditorUtility.SetDirty(countryData);
			// The asset isn't part of any scene, but Unity's "save" action saves the active
			// scene plus any dirty assets together - marking the scene dirty too keeps a plain
			// Ctrl+S from silently skipping this asset.
			EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

		}

	}
}
