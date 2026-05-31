using Amazon.BedrockRuntime;

namespace BedrockWorkspaceExplorer.Abstractions;

/// <summary>
/// Defines the contract for the autonomous folder intelligence agent.
/// Implementations are responsible for orchestrating the Bedrock Converse API
/// loop, managing conversational history, and dispatching tool invocations.
/// </summary>
/// <remarks>
/// This interface is intentionally narrow to remain portable across console apps,
/// ASP.NET Web APIs, background workers, or any DI container.
/// </remarks>
public interface IBedrockAgentService
{
    /// <summary>
    /// Runs the autonomous agent loop against the specified directory, using the
    /// given instruction as the opening user message.
    /// </summary>
    /// <param name="rootFolderPath">
    /// Absolute path to the local directory the agent is allowed to inspect.
    /// All tool calls are sandboxed to this root.
    /// </param>
    /// <param name="userInstruction">
    /// The natural-language task description sent as the initial user turn.
    /// </param>
    /// <returns>
    /// The final text response produced by the model once it has finished
    /// reasoning (StopReason == "end_turn") or after the cycle cap is reached.
    /// </returns>
    Task<string> AnalyzeFolderAsync(string rootFolderPath, string userInstruction);
}
