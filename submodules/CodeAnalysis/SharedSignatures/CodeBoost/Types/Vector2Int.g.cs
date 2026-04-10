using System;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct Vector2Int
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
	    /// Creates a new Vector2Int using values.
	    /// </summary>
	    public Vector2Int(int x = 0, int y = 0)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector2Int(Vector2Int vector2Int)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector2Int(Vector2 vector2, MidpointRounding rounding)
	    {
	    }
	}
}
