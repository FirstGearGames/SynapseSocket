using System;
using System.Numerics;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct FloatRange2D
	{
	    public FloatRange X;
	    public FloatRange Y;
	    public FloatRange2D(FloatRange x, FloatRange y)
	    {
	    }
	
	    public FloatRange2D(float xMin, float xMax, float yMin, float yMax)
	    {
	    }
	
	    public Vector2 Clamp(Vector2 original)
	    {
	        return default !;
	    }
	
	    public Vector3 Clamp(Vector3 original)
	    {
	        return default !;
	    }
	
	    public float ClampX(float original)
	    {
	        return default !;
	    }
	
	    public float ClampY(float original)
	    {
	        return default !;
	    }
	}
}
