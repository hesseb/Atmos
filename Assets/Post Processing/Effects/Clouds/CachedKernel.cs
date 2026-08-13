using UnityEngine;

namespace Clouds
{
	/// <summary>
	/// A FindKernel result, held until the compute shader it came from is replaced.
	///
	/// FindKernel is a name lookup across the interop boundary, and both cloud renderers were doing
	/// one per dispatch per frame - four between them - to re-derive an index that had not changed
	/// since the last domain reload.
	///
	/// Keyed on the ComputeShader reference rather than resolved once at enable, because the compute
	/// fields on both effects are public and can be repointed from the inspector. A kernel index is
	/// only meaningful against the shader it was looked up in, so a stale one does not fail loudly -
	/// it dispatches a different kernel.
	///
	/// Its own file rather than a nested type, so the volumetric and the baseline share one
	/// definition instead of one owning it and the other reaching into it.
	/// </summary>
	public struct CachedKernel
	{
		ComputeShader source;
		int index;

		public int Of(ComputeShader compute, string name)
		{
			if (compute != source)
			{
				source = compute;
				index = compute.FindKernel(name);
			}
			return index;
		}
	}
}
