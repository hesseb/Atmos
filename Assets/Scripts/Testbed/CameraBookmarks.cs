using UnityEngine;

/// <summary>
/// A named, key-bound set of camera views.
///
/// This is a ScriptableObject rather than fields on <see cref="TestbedCamera"/> for one
/// practical reason: Unity discards changes made to scene components when play mode
/// exits, but changes to an *asset* persist. Capturing a view is something you naturally
/// want to do while flying around in play mode, so the bookmarks have to be an asset or
/// every capture would be lost on stop.
///
/// It is also the natural home for the measurement harness's camera path list - a set of
/// named, exactly reproducible views is precisely what the fixed camera paths need.
///
/// Create via Assets > Create > Testbed > Camera Bookmarks, then assign it to
/// TestbedCamera.
/// </summary>
[CreateAssetMenu(menuName = "Testbed/Camera Bookmarks", fileName = "Camera Bookmarks")]
public class CameraBookmarks : ScriptableObject
{
	[System.Serializable]
	public struct Bookmark
	{
		public string label;
		public KeyCode key;
		// Empty slots are skipped rather than jumping to an all-zero view, which would
		// put the camera at the planet's surface.
		public bool assigned;
		public TestbedCamera.CameraView view;
	}

	public Bookmark[] bookmarks = DefaultSet();

	public int Count => bookmarks != null ? bookmarks.Length : 0;

	public bool TryGetIndexForKey(KeyCode key, out int index)
	{
		index = -1;
		if (bookmarks == null || key == KeyCode.None) { return false; }

		for (int i = 0; i < bookmarks.Length; i++)
		{
			if (bookmarks[i].key == key)
			{
				index = i;
				return true;
			}
		}
		return false;
	}

	public bool TryGetView(int index, out TestbedCamera.CameraView view)
	{
		view = default;
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return false; }
		if (!bookmarks[index].assigned) { return false; }

		view = bookmarks[index].view;
		return true;
	}

	public KeyCode KeyAt(int index)
	{
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return KeyCode.None; }
		return bookmarks[index].key;
	}

	public bool IsAssigned(int index)
	{
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return false; }
		return bookmarks[index].assigned;
	}

	public string LabelAt(int index)
	{
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return "?"; }

		string label = bookmarks[index].label;
		return string.IsNullOrEmpty(label) ? $"slot {index + 1}" : label;
	}

	/// <summary>
	/// Stores a view in a slot. Marks the asset dirty so an in-play-mode capture is
	/// written to disk on the next project save rather than lingering only in memory.
	/// </summary>
	public void Capture(int index, TestbedCamera.CameraView view)
	{
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return; }

		bookmarks[index].view = view;
		bookmarks[index].assigned = true;

#if UNITY_EDITOR
		UnityEditor.EditorUtility.SetDirty(this);
#endif
	}

	public void Clear(int index)
	{
		if (bookmarks == null || index < 0 || index >= bookmarks.Length) { return; }

		bookmarks[index].assigned = false;
#if UNITY_EDITOR
		UnityEditor.EditorUtility.SetDirty(this);
#endif
	}

	static Bookmark[] DefaultSet()
	{
		return new[]
		{
			Empty("Slot 1", KeyCode.Z),
			Empty("Slot 2", KeyCode.X),
			Empty("Slot 3", KeyCode.C),
			Empty("Slot 4", KeyCode.V),
		};
	}

	static Bookmark Empty(string label, KeyCode key)
	{
		return new Bookmark { label = label, key = key, assigned = false };
	}

	void Reset()
	{
		bookmarks = DefaultSet();
	}
}
