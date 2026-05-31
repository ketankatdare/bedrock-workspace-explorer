using System.Text;
using Amazon.BedrockRuntime;
using Amazon.BedrockRuntime.Model;
using Amazon.Runtime.Documents;
using BedrockWorkspaceExplorer.Abstractions;
using BedrockWorkspaceExplorer.Configuration;
using BedrockWorkspaceExplorer.Tools;

namespace BedrockWorkspaceExplorer.Services;

/// <summary>
/// Implements <see cref="IBedrockAgentService"/> using the AWS Bedrock Converse
/// API. Manages a private per-invocation conversational history list and
/// orchestrates the tool-use / response loop up to a fixed cycle cap.
/// </summary>
/// <remarks>
/// <para>
/// The service is intentionally stateless between calls to
/// <see cref="AnalyzeFolderAsync"/> — a fresh <see cref="List{Message}"/>
/// history is constructed on every invocation, making the service safe to share
/// across concurrent requests in a Web API scenario.
/// </para>
/// <para>
/// The <paramref name="client"/> dependency is injected via the primary
/// constructor, keeping the class testable without any concrete AWS calls.
/// </para>
/// </remarks>
public sealed class BedrockAgentService(IAmazonBedrockRuntime client, BedrockSettings settings) : IBedrockAgentService
{
    private const int MaxCycles = 5;

    // StopReason string values returned by the Converse API.
    private const string StopReasonEndTurn = "end_turn";
    private const string StopReasonToolUse = "tool_use";

    // ─────────────────────────────────────────────────────────────────────────
    // IBedrockAgentService
    // ─────────────────────────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<string> AnalyzeFolderAsync(string rootFolderPath, string userInstruction)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootFolderPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(userInstruction);

        if (!Directory.Exists(rootFolderPath))
            throw new DirectoryNotFoundException($"Root folder not found: {rootFolderPath}");

        // ── Conversational history ────────────────────────────────────────────
        // Each element in this list represents one turn (user or assistant).
        // We carry the full history forward on every Converse call so the model
        // retains context across tool-use cycles.
        var history = new List<Message>
        {
            new()
            {
                Role    = ConversationRole.User,
                Content = [new ContentBlock { Text = userInstruction }]
            }
        };

        // ── Tool configuration (built once, reused across cycles) ─────────────
        var toolConfig = new ToolConfiguration
        {
            Tools = AgentTools.BuildToolList()
        };

        // ── System prompt ─────────────────────────────────────────────────────
        var systemPrompt = new List<SystemContentBlock>
        {
            new()
            {
                Text =
                    "You are a meticulous Universal Folder Intelligence Agent. " +
                    "You have been given access to a local directory on the user's machine. " +
                    "Your mission is to explore the directory using the tools provided, " +
                    "reason carefully about what you find, and produce a clear, structured " +
                    "summary of the folder's layout, apparent purpose, and contents. " +
                    "Always begin by listing the root directory before diving deeper. " +
                    "If you find a README, manifest, config, or notes file, read it — " +
                    "it is likely the best signal for understanding the folder's purpose. " +
                    "Never guess — always verify with a tool call first."
            }
        };

        // ── Main orchestration loop ───────────────────────────────────────────
        string finalAnswer   = string.Empty;
        int    cyclesElapsed = 0;

        Console.WriteLine();
        Console.WriteLine("  ┌─ Agent loop started ───────────────────────────────────────┐");

        while (cyclesElapsed < MaxCycles)
        {
            cyclesElapsed++;
            Console.WriteLine($"  │  Cycle {cyclesElapsed}/{MaxCycles} — calling Bedrock …");

            var request = new ConverseRequest
            {
                ModelId    = settings.ModelId,
                System     = systemPrompt,
                Messages   = history,
                ToolConfig = toolConfig,
                InferenceConfig = new InferenceConfiguration
                {
                    MaxTokens   = 4096,
                    Temperature = 0f
                }
            };

            ConverseResponse response = await client.ConverseAsync(request);
            Message assistantMessage  = response.Output.Message;

            // Always persist the model's reply into history so the next cycle
            // sees the full context (including any ToolUseBlocks it emitted).
            history.Add(assistantMessage);

            string stopReason = response.StopReason.Value;

            // ── Case A: Model has finished reasoning ──────────────────────────
            if (stopReason == StopReasonEndTurn)
            {
                finalAnswer = ExtractTextFromMessage(assistantMessage);
                Console.WriteLine($"  │  ✓ Model finished. Stop reason: end_turn.");
                break;
            }

            // ── Case B: Model wants to call one or more tools ─────────────────
            if (stopReason == StopReasonToolUse)
            {
                var toolResultBlocks = new List<ContentBlock>();

                foreach (ContentBlock block in assistantMessage.Content)
                {
                    if (block.ToolUse is not { } toolUse)
                        continue;

                    Console.WriteLine($"  │    → Tool call: {toolUse.Name}({FormatInput(toolUse.Input)})");

                    string toolResult = AgentTools.Invoke(toolUse.Name, toolUse.Input, rootFolderPath);

                    Console.WriteLine($"  │      ← Result: {Truncate(toolResult, 120)}");

                    toolResultBlocks.Add(new ContentBlock
                    {
                        ToolResult = new ToolResultBlock
                        {
                            ToolUseId = toolUse.ToolUseId,
                            Content   =
                            [
                                new ToolResultContentBlock { Text = toolResult }
                            ]
                        }
                    });
                }

                // Feed all tool results back as a single user turn.
                if (toolResultBlocks.Count > 0)
                {
                    history.Add(new Message
                    {
                        Role    = ConversationRole.User,
                        Content = toolResultBlocks
                    });
                }

                // Loop continues — model will now process the tool results.
                continue;
            }

            // ── Case C: Unexpected stop reason (e.g., max_tokens, content_filtered)
            Console.WriteLine($"  │  ⚠  Unexpected stop reason: {stopReason}. Stopping loop.");
            finalAnswer = ExtractTextFromMessage(assistantMessage);
            break;
        }

        if (cyclesElapsed >= MaxCycles && string.IsNullOrWhiteSpace(finalAnswer))
        {
            Console.WriteLine($"  │  ⚠  Cycle cap ({MaxCycles}) reached. Returning last accumulated text.");
            finalAnswer = ExtractTextFromMessage(history.LastOrDefault(m => m.Role == ConversationRole.Assistant));
        }

        Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
        Console.WriteLine();

        return finalAnswer;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Private helpers
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Concatenates all Text blocks in a message into a single string,
    /// ignoring non-text content blocks (e.g., ToolUse blocks).
    /// </summary>
    private static string ExtractTextFromMessage(Message? message)
    {
        if (message is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (ContentBlock block in message.Content)
        {
            if (!string.IsNullOrWhiteSpace(block.Text))
                sb.AppendLine(block.Text);
        }
        return sb.ToString().Trim();
    }

    /// <summary>
    /// Produces a concise, single-line representation of a tool's Document
    /// input for console progress logging.
    /// </summary>
    private static string FormatInput(Document input)
    {
        try
        {
            var dict = input.AsDictionary();
            return string.Join(", ", dict.Select(kv =>
                $"{kv.Key}={Truncate(kv.Value.ToString() ?? "", 60)}"));
        }
        catch
        {
            return input.ToString() ?? "<unknown>";
        }
    }

    /// <summary>Truncates a string to <paramref name="maxLen"/> chars with an ellipsis.</summary>
    private static string Truncate(string s, int maxLen) =>
        s.Length <= maxLen ? s : s[..maxLen] + "…";
}
