using System;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct Vector4Int
	{
	    /// <summary>
	    /// X value.
	    /// </summary>
	    public int X;
	    /// <summary>
	    /// Y value.
	    /// </summary>
	    public int Y;
	    /// <summary>
	    /// Z value.
	    /// </summary>
	    public int Z;
	    /// <summary>
	    /// W value.
	    /// </summary>
	    public int W;
	    /// <summary>
	    /// Creates a new Vector2Int using values.
	    /// </summary>
	    public Vector4Int(int x = 0, int y = 0, int z = 0, int w = 0)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector4Int(Vector4Int vector4Int)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector4Int(Vector4 vector4, MidpointRounding rounding)
	    {
	    }
	}
}
