#pragma warning disable CS8632 // The annotation for nullable reference types should only be used in code within a '#nullable' annotations context.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeBoost.Logging;
using CodeBoost.Types;
using System;
using System.Threading.Tasks;
namespace CodeBoost.Extensions
{
 public static class IOExtensions
	{
	    /// <summary>
	    /// How to format a platform path.
	    /// </summary>
	    public enum PathFormattingType
	    {
	        /// <summary>
	        /// Do not format the path.
	        /// </summary>
	        Disabled = 0,
	        /// <summary>
	        /// Formats the path for the current platform type.
	        /// </summary>
	        FormatToPlatform = 1
	    }
	
	    /// <summary>
	    /// How to write data to a file.
	    /// </summary>
	    public enum WriteType
	    {
	        /// <summary>
	        /// Appends onto current data.
	        /// </summary>
	        Append = 0,
	        /// <summary>
	        /// Replaces existing data with new.
	        /// </summary>
	        Create = 1
	    }
	
	    /// <summary>
	    /// Writes a text value to a file path.
	    /// </summary>
	    public static void WriteToFile(string value, string path, WriteType writeType = WriteType.Create, PathFormattingType pathFormattingType = PathFormattingType.FormatToPlatform)
	    {
	    }
	
	    /// <summary>
	    /// Formats a file path to the current platform.
	    /// </summary>
	    public static string FormatPlatformPath(string path)
	    {
	        return default !;
	    }
	
	    /// <summary>
	    /// Returns files on a path which matches a value.
	    /// </summary>
	    public static List<string> GetDirectoryFilesRecursively(string path, string searchPattern) => default;
	    /// <summary>
	    /// Returns files on a path which matches a value.
	    /// </summary>
	    public static List<string> GetDirectoryFilesRecursively(string path, string searchPattern, List<string>? excludedPaths)
	    {
	        return default !;
	    }
	}
}
