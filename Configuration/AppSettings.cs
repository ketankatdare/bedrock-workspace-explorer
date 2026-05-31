namespace BedrockWorkspaceExplorer.Configuration;

/// <summary>Root-bound configuration from <c>appsettings.json</c>.</summary>
public sealed class AppSettings
{
    public AwsSettings Aws { get; set; } = new();
    public BedrockSettings Bedrock { get; set; } = new();
}

public sealed class AwsSettings
{
    /// <summary>
    /// Named profile from <c>~/.aws/credentials</c>. When empty, the default
    /// credential provider chain is used (env vars, instance profile, etc.).
    /// </summary>
    public string Profile { get; set; } = string.Empty;

    /// <summary>AWS region system name (e.g. <c>us-east-1</c>).</summary>
    public string Region { get; set; } = "us-east-1";
}

public sealed class BedrockSettings
{
    /// <summary>Bedrock model ID passed to the Converse API.</summary>
    public string ModelId { get; set; } = "amazon.nova-micro-v1:0";
}
