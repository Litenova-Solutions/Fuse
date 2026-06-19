using Fuse.Emission.Models;

namespace Fuse.Emission;

/// <summary>
///     Generates base filenames and finalizes output files with token counts and part numbers.
/// </summary>
public sealed class OutputNamingService
{
    /// <summary>
    ///     Determines the base filename for output files from emission options.
    /// </summary>
    /// <param name="options">The emission options.</param>
    /// <returns>The base filename without extension or path.</returns>
    public string GetBaseFileName(EmissionOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.OutputFileName))
        {
            var baseFileName = options.OutputFileName;

            if (Path.HasExtension(baseFileName))
            {
                baseFileName = Path.GetFileNameWithoutExtension(baseFileName);
            }

            return baseFileName;
        }

        var dirName = Path.GetFileName(options.OutputDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Replace('.', '_');

        if (string.IsNullOrWhiteSpace(dirName))
        {
            dirName = "fuse";
        }

        var dateStamp = DateTime.Now.ToString("yyyy-MM-dd");
        var timeStamp = DateTime.Now.ToString("HHmm");

        return $"{dirName}_{dateStamp}_{timeStamp}";
    }

    /// <summary>
    ///     Formats a token count into a readable suffix (for example, <c>554k</c> or <c>500t</c>).
    /// </summary>
    /// <param name="count">The raw token count.</param>
    /// <returns>A formatted string representation.</returns>
    public static string FormatTokenCount(long count)
    {
        if (count < 1000)
        {
            return $"{count}t";
        }

        return $"{count / 1000.0:0}k";
    }

    /// <summary>
    ///     Builds the final output filename for a part.
    /// </summary>
    /// <param name="baseName">The base filename.</param>
    /// <param name="part">The part number.</param>
    /// <param name="tokenCount">The token count for this part.</param>
    /// <param name="isMultiPart">Whether this file is part of a multi-part set.</param>
    /// <returns>The filename including extension.</returns>
    public static string BuildPartFileName(string baseName, int part, long tokenCount, bool isMultiPart)
    {
        var tokenString = FormatTokenCount(tokenCount);
        var partSuffix = isMultiPart ? $"_part{part}" : string.Empty;
        return $"{baseName}{partSuffix}_{tokenString}.txt";
    }

    /// <summary>
    ///     Renames a temporary file to its final output path, handling overwrite and collision fallback.
    /// </summary>
    /// <param name="tempPath">The path to the temporary file.</param>
    /// <param name="directory">The output directory.</param>
    /// <param name="baseName">The base filename.</param>
    /// <param name="part">The part number.</param>
    /// <param name="tokenCount">The token count for this part.</param>
    /// <param name="overwrite">Whether to overwrite existing files.</param>
    /// <param name="isMultiPart">Whether this file is part of a multi-part set.</param>
    /// <returns>The full path to the finalized file.</returns>
    public string FinalizeFile(
        string tempPath,
        string directory,
        string baseName,
        int part,
        long tokenCount,
        bool overwrite,
        bool isMultiPart)
    {
        var fileName = BuildPartFileName(baseName, part, tokenCount, isMultiPart);
        var finalPath = Path.Combine(directory, fileName);

        if (File.Exists(finalPath))
        {
            if (!overwrite)
            {
                fileName = $"{baseName}{(isMultiPart ? $"_part{part}" : string.Empty)}_{FormatTokenCount(tokenCount)}_{DateTime.Now:mmss}.txt";
                finalPath = Path.Combine(directory, fileName);
            }
            else
            {
                File.Delete(finalPath);
            }
        }

        File.Move(tempPath, finalPath);
        return finalPath;
    }
}
