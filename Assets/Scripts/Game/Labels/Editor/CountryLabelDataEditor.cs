using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;

[CustomEditor(typeof(CountryLabelData))]
public class CountryLabelDataEditor : Editor
{
	public override void OnInspectorGUI()
	{
		base.OnInspectorGUI();

		CountryLabelData labelData = target as CountryLabelData;

		EditorGUILayout.Space();

		if (labelData.source == null)
		{
			EditorGUILayout.HelpBox("Assign the CountryData asset to bake from.", MessageType.Info);
			return;
		}

		if (GUILayout.Button("Bake Anchors"))
		{
			BakeAnchors(labelData);
		}

		if (labelData.Count > 0)
		{
			int failed = 0;
			float maxRadius = 0f;
			foreach (var entry in labelData.entries)
			{
				if (entry.bakeFailed) { failed++; }
				maxRadius = Mathf.Max(maxRadius, entry.angularRadius);
			}

			EditorGUILayout.HelpBox(
				$"{labelData.Count} entries, {failed} failed.\n" +
				$"Largest angular radius {maxRadius * Mathf.Rad2Deg:0.0} degrees.",
				failed > 5 ? MessageType.Warning : MessageType.Info);
		}
	}

	static void BakeAnchors(CountryLabelData labelData)
	{
		var timer = System.Diagnostics.Stopwatch.StartNew();

		Undo.RecordObject(labelData, "Bake Country Label Anchors");

		CountryLabelBaker.BakeReport report;
		try
		{
			labelData.entries = CountryLabelBaker.Bake(labelData.source, out report,
				(fraction, name) => EditorUtility.DisplayProgressBar(
					"Baking country label anchors", name, fraction));
		}
		finally
		{
			EditorUtility.ClearProgressBar();
		}

		// SetDirty in addition to RecordObject, and marking the scene dirty, for the same
		// reason CountryDataEditor does: ScriptableObject changes otherwise don't reliably
		// reach disk.
		EditorUtility.SetDirty(labelData);
		EditorSceneManager.MarkSceneDirty(EditorSceneManager.GetActiveScene());

		string failures = report.failedNames.Count > 0
			? "\n  fell back to centroid: " + string.Join(", ", report.failedNames)
			: "";

		Debug.Log(
			$"[Label bake] {report.baked} anchors in {timer.ElapsedMilliseconds} ms\n" +
			$"  projection failed {report.failed}   empty/degenerate shape {report.emptyShape}" +
			failures, labelData);
	}
}
