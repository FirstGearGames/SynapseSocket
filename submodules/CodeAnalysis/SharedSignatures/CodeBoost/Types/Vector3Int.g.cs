using System;
using System.Numerics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	public struct Vector3Int
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
	    /// Creates a new Vector2Int using values.
	    /// </summary>
	    public Vector3Int(int x = 0, int y = 0, int z = 0)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector3Int(Vector3Int vector3Int)
	    {
	    }
	
	    /// <summary>
	    /// Creates a new Vector2Int using value.
	    /// </summary>
	    public Vector3Int(Vector3 vector3, MidpointRounding rounding)
	    {
	    }
	}
}
