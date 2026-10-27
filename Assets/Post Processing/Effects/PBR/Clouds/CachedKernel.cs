using UnityEngine;

namespace Clouds
{
	public struct CachedKernel
	{
		ComputeShader source;
		int index;
		uint groupSizeX;
		uint groupSizeY;

		public int Of( ComputeShader compute, string name )
		{
			if ( compute != source )
			{
				source = compute;
				index = compute.FindKernel( name );
				compute.GetKernelThreadGroupSizes( index, out groupSizeX, out groupSizeY, out _ );
			}
			return index;
		}

		public void Dispatch( int width, int height )
		{
			source.Dispatch( index,
				Mathf.CeilToInt( width / (float)groupSizeX ),
				Mathf.CeilToInt( height / (float)groupSizeY ), 1 );
		}
	}
}
