using UnityEngine;

namespace Clouds
{
	/// <summary>
	/// A FindKernel result, cached until the compute shader it came from is replaced - avoids
	/// re-deriving an unchanged index every dispatch. Keyed on the ComputeShader reference rather
	/// than resolved once at enable, since the compute fields are public and can be repointed from
	/// the inspector, and a stale index would silently dispatch the wrong kernel.
	/// </summary>
	public struct CachedKernel
	{
		ComputeShader source;
		int index;

		public int Of( ComputeShader compute, string name )
		{
			if ( compute != source )
			{
				source = compute;
				index = compute.FindKernel( name );
			}
			return index;
		}
	}
}
