using System;
using System.Drawing;
using System.Runtime.InteropServices;
using CodeBoost.Mathematics;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace CodeBoost.Types
{
 [Serializable]
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct ColorUnified
	{
	    /// <summary>
	    /// R value for the color as a byte, 0-255.
	    /// </summary>
	    public byte R;
	    /// <summary>
	    /// G value for the color as a byte, 0-255.
	    /// </summary>
	    public byte G;
	    /// <summary>
	    /// B value for the color as a byte, 0-255.
	    /// </summary>
	    public byte B;
	    /// <summary>
	    /// A value for the color as a byte, 0-255.
	    /// </summary>
	    public byte A;
	    /// <summary>
	    /// R value for the color as a single, 0-1f.
	    /// </summary>
	    public float Rf;
	    /// <summary>
	    /// G value for the color as a single, 0-1f.
	    /// </summary>
	    public float Gf;
	    /// <summary>
	    /// B value for the color as a single, 0-1f.
	    /// </summary>
	    public float Bf;
	    /// <summary>
	    /// A value for the color as a single, 0-1f.
	    /// </summary>
	    public float Af;
	    public ColorUnified(byte r, byte g, byte b, byte a)
	    {
	    }
	
	    public ColorUnified(Color color)
	    {
	    }
	
	    public ColorUnified(float r, float g, float b, float a = 1f)
	    {
	    }
	
	    /// <summary>
	    /// Returns a Color using the current values.
	    /// </summary>
	    /// <returns></returns>
	    public Color GetColor() => default;
	}
}
