using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace CodeAnalysis.Logging;

public static class CodeAnalysisLogger
{
    /// <summary>
    /// Messages by output path suffixes.
    /// </summary>
    private static readonly Dictionary<string, List<string>> Messages = [];
    private static string _outputPathPrefix;
    private const string FileExtension = ".txt";

    private static void AddMessage(string fileSuffix, string message)
    {
        if (string.IsNullOrEmpty(fileSuffix))
            fileSuffix = "_";

        if (!Messages.TryGetValue(fileSuffix, out List<string> messages))
        {
            messages = [];
            Messages.Add(fileSuffix, messages);
        }

        messages.Add(message);
    }

    /// <summary>
    /// Sets the output path. The .txt should be removed.
    /// </summary>
    public static void SetOutputPath(string outputFilePath) => _outputPathPrefix = outputFilePath;

    public static void LogInformation(string message) => AddMessage("", $"Information: {message}");
    public static void LogWarning(string message) => AddMessage("", $"Warning: {message}");
    public static void LogError(string message) => AddMessage("", $"Error: {message}");

    public static void LogCode(string message)
    {
        AddMessage("", "");
        AddMessage("", message);
        AddMessage("", "");
    }

    public static void LogCode(string fileSuffix, string message)
    {
        AddMessage(fileSuffix, "");
        AddMessage(fileSuffix, message);
        AddMessage(fileSuffix, "");
    }

    public static void WriteToFile()
    {
        #pragma warning disable RS1035

        foreach (KeyValuePair<string, List<string>> kvp in Messages)
        {
            string fullPath = $"{_outputPathPrefix}{kvp.Key}{FileExtension}";
                
            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                try
                {
                    DateTime startTime = DateTime.Now;
                    File.Delete(fullPath);

                    while (File.Exists(fullPath))
                    {
                        Thread.Sleep(100);
                        if ((DateTime.Now - startTime).TotalSeconds > 3)
                            break;
                    }
                }
                catch
                {
                    // ignored
                }

                File.WriteAllLines(fullPath, kvp.Value);
            }
                
            kvp.Value.Clear();
        }


        #pragma warning restore RS1035
    }
}