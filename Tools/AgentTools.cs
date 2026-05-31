using System.Text.Json;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;

namespace BedrockWorkspaceExplorer.Tools;

/// <summary>
/// Owns the structural tool specifications that are registered with the Bedrock
/// Converse API, and provides a single dispatch entry-point for executing them.
///
/// All filesystem access is sandboxed to the root directory provided at dispatch
/// time, preventing any path-traversal exploitation via model-generated inputs.
/// </summary>
public static class AgentTools
{
    // ─────────────────────────────────────────────────────────────────────────
    // Tool name constants — single source of truth used in both spec building
    // and dispatch, so a typo can never cause a silent miss.
    // ─────────────────────────────────────────────────────────────────────────

    public const string ListDirectoryContentsName = "ListDirectoryContents";
    public const string ReadFileSnippetName = "ReadFileSnippet";

    // ─────────────────────────────────────────────────────────────────────────
    // Tool list builder
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds the <see cref="List{Tool}"/> required by
    /// <see cref="Amazon.BedrockRuntime.Model.ToolConfiguration"/>.
    /// Each tool's input schema is described using Bedrock's JSON Schema subset
    /// serialised via <see cref="Document.FromObject"/>.
    /// </summary>
    public static List<Tool> BuildToolList() =>
    [
        new Tool
        {
            ToolSpec = new ToolSpecification
            {
                Name        = ListDirectoryContentsName,
                Description =
                    "Lists all files and immediate sub-directories inside a directory. " +
                    "Accepts a path relative to the root folder being inspected. " +
                    "Pass an empty string or \".\" to list the root itself. " +
                    "Returns a JSON array of entry names.",
                InputSchema = new ToolInputSchema
                {
                    Json = Document.FromObject(new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["relativePath"] = new Dictionary<string, object>
                            {
                                ["type"]        = "string",
                                ["description"] = "Path relative to the root folder. Use empty string or \".\" for the root itself.",
                            }
                        },
                        ["required"] = new List<string> { "relativePath" }
                    })
                }
            }
        },

        new Tool
        {
            ToolSpec = new ToolSpecification
            {
                Name        = ReadFileSnippetName,
                Description =
                    "Reads the first 2 000 characters of a text file inside the root folder. " +
                    "Accepts a path relative to the root folder. " +
                    "Useful for reading READMEs, manifests, config files, and notes " +
                    "to understand the nature and purpose of the directory.",
                InputSchema = new ToolInputSchema
                {
                    Json = Document.FromObject(new Dictionary<string, object>
                    {
                        ["type"] = "object",
                        ["properties"] = new Dictionary<string, object>
                        {
                            ["filePath"] = new Dictionary<string, object>
                            {
                                ["type"]        = "string",
                                ["description"] = "Path to the file, relative to the root folder.",
                            }
                        },
                        ["required"] = new List<string> { "filePath" }
                    })
                }
            }
        }
    ];

    // ─────────────────────────────────────────────────────────────────────────
    // Central dispatch
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Dispatches a tool call by name, extracts arguments from the Bedrock
    /// <see cref="Document"/> payload, and returns the raw string result that
    /// will be wrapped in a <c>ToolResultBlock</c> and sent back to the model.
    /// </summary>
    /// <param name="toolName">The name returned by the model's ToolUseBlock.</param>
    /// <param name="input">The argument document from the model's ToolUseBlock.</param>
    /// <param name="rootPath">Absolute root directory — used as the sandbox boundary.</param>
    public static string Invoke(string toolName, Document input, string rootPath)
    {
        var args = input.AsDictionary();

        return toolName switch
        {
            ListDirectoryContentsName => ListDirectoryContents(
                relativePath: GetStringArg(args, "relativePath"),
                rootPath: rootPath),

            ReadFileSnippetName => ReadFileSnippet(
                relativeFilePath: GetStringArg(args, "filePath"),
                rootPath: rootPath),

            _ => $"{{\"error\": \"Unknown tool: {toolName}\"}}"
        };
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Tool implementations
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns a JSON array of file system entry names (files and directories)
    /// in the specified directory. Subdirectory names are suffixed with <c>/</c>
    /// so the model can tell them apart from files at a glance.
    /// </summary>
    private static string ListDirectoryContents(string relativePath, string rootPath)
    {
        try
        {
            string resolvedPath = ResolveSafePath(relativePath, rootPath);

            if (!Directory.Exists(resolvedPath))
                return JsonSerializer.Serialize(new { error = $"Directory not found: {relativePath}" });

            string[] dirs  = Directory.GetDirectories(resolvedPath)
                                       .Select(d => Path.GetFileName(d) + "/")
                                       .OrderBy(n => n)
                                       .ToArray();

            string[] files = Directory.GetFiles(resolvedPath)
                                      .Select(f => Path.GetFileName(f)!)
                                      .OrderBy(n => n)
                                      .ToArray();

            string[] entries = [.. dirs, .. files];

            return JsonSerializer.Serialize(entries);
        }
        catch (UnauthorizedAccessException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Access denied: {ex.Message}" });
        }
        catch (DirectoryNotFoundException ex)
        {
            return JsonSerializer.Serialize(new { error = $"Directory not found: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { error = $"Unexpected error: {ex.Message}" });
        }
    }

    /// <summary>
    /// Reads the first 2 000 characters of a text file.
    /// The path is resolved relative to <paramref name="rootPath"/> and
    /// validated to prevent the model from escaping the sandbox.
    /// </summary>
    private static string ReadFileSnippet(string relativeFilePath, string rootPath)
    {
        try
        {
            string resolvedPath = ResolveSafePath(relativeFilePath, rootPath);

            // ── Path-traversal guard ──────────────────────────────────────────
            string canonicalRoot     = Path.GetFullPath(rootPath);
            string canonicalResolved = Path.GetFullPath(resolvedPath);

            if (!canonicalResolved.StartsWith(canonicalRoot, StringComparison.OrdinalIgnoreCase))
                return "Access denied: path escapes the root folder boundary.";

            if (!File.Exists(canonicalResolved))
                return $"File not found: {relativeFilePath}";

            const int MaxChars = 2_000;

            using var reader = new StreamReader(canonicalResolved);
            char[] buffer = new char[MaxChars];
            int charsRead = reader.Read(buffer, 0, MaxChars);

            string snippet = new string(buffer, 0, charsRead);

            bool truncated = reader.Peek() != -1;
            return truncated
                ? snippet + $"\n\n[... truncated — showing first {MaxChars} characters ...]"
                : snippet;
        }
        catch (UnauthorizedAccessException ex)
        {
            return $"Access denied: {ex.Message}";
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Combines <paramref name="relativePath"/> with <paramref name="rootPath"/>,
    /// treating empty / dot paths as the root directory itself.
    /// </summary>
    private static string ResolveSafePath(string relativePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath is ".")
            return rootPath;

        // Strip any leading directory-separator characters the model might supply.
        string cleaned = relativePath.TrimStart('/', '\\');
        return Path.Combine(rootPath, cleaned);
    }

    /// <summary>
    /// Safely extracts a string value from the tool's argument dictionary,
    /// returning an empty string if the key is absent or the value is not a string.
    /// </summary>
    private static string GetStringArg(IReadOnlyDictionary<string, Document> args, string key)
    {
        if (args.TryGetValue(key, out Document value))
            return value.IsString() ? value.AsString() : value.ToString() ?? string.Empty;

        return string.Empty;
    }
}
