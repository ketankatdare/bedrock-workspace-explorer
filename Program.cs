using Amazon;
using Amazon.BedrockRuntime;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using BedrockWorkspaceExplorer.Configuration;
using BedrockWorkspaceExplorer.Services;
using Microsoft.Extensions.Configuration;

// ─────────────────────────────────────────────────────────────────────────────
//  Universal Folder Intelligence Agent — Entry Point
// ─────────────────────────────────────────────────────────────────────────────
//
//  Prerequisites:
//    • AWS credentials configured (env vars, ~/.aws/credentials, or IAM role).
//    • The chosen region must have access to your configured Bedrock model.
//    • IAM permission: bedrock:InvokeModel on the model ARN.
//
//  Usage:
//    dotnet run                            ← analyses the project's own root
//    dotnet run -- "C:\path\to\folder"     ← analyses the supplied path
//
// ─────────────────────────────────────────────────────────────────────────────

var settings = LoadSettings();
PrintBanner(settings.Bedrock.ModelId);

// ── Resolve the target directory ──────────────────────────────────────────────
// Priority: CLI argument → project root (self-analysis fallback)
string rootFolder = args.Length > 0
    ? Path.GetFullPath(args[0])
    : Path.GetFullPath(AppContext.BaseDirectory              // bin/Debug/net10.0/
          .Split(["bin"], StringSplitOptions.None)[0]        // strip trailing build artefacts
          .TrimEnd(Path.DirectorySeparatorChar));

if (!Directory.Exists(rootFolder))
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"  ERROR: Directory not found — {rootFolder}");
    Console.ResetColor();
    return 1;
}

Console.ForegroundColor = ConsoleColor.Cyan;
Console.WriteLine($"  Target folder : {rootFolder}");
Console.ResetColor();

// ── Build the AWS client ──────────────────────────────────────────────────────
var region = RegionEndpoint.GetBySystemName(settings.Aws.Region);
IAmazonBedrockRuntime bedrockClient;
if (string.IsNullOrWhiteSpace(settings.Aws.Profile))
{
    bedrockClient = new AmazonBedrockRuntimeClient(region);
}
else
{
    var profileChain = new CredentialProfileStoreChain();
    if (!profileChain.TryGetAWSCredentials(settings.Aws.Profile, out AWSCredentials? credentials))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.Error.WriteLine($"  ERROR: AWS profile not found — {settings.Aws.Profile}");
        Console.ResetColor();
        return 1;
    }

    bedrockClient = new AmazonBedrockRuntimeClient(credentials, region);
}

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine(string.IsNullOrWhiteSpace(settings.Aws.Profile)
    ? "  AWS credentials: default chain"
    : $"  AWS profile      : {settings.Aws.Profile}");
Console.WriteLine($"  AWS region       : {settings.Aws.Region}");
Console.WriteLine($"  Bedrock model    : {settings.Bedrock.ModelId}");
Console.ResetColor();

// ── Wire up the agent service ─────────────────────────────────────────────────
var agentService = new BedrockAgentService(bedrockClient, settings.Bedrock);

// ── The mandated exploration prompt ──────────────────────────────────────────
const string UserInstruction =
    "Explore this root directory. Look at the files and subfolders to deduce what kind of " +
    "folder this is (e.g., a software repository, a document archive, a media collection, " +
    "a backup folder, or a mixed workspace). Do not assume it is a code repository. " +
    "If you find key files that help explain the contents (like READMEs, manifest files, " +
    "text notes, or document summaries), read their contents to gain context. " +
    "Provide a clear, structured summary of the folder's layout, its apparent purpose, " +
    "and a breakdown of what it contains.";

// ── Run the agent ─────────────────────────────────────────────────────────────
Console.ForegroundColor = ConsoleColor.Yellow;
Console.WriteLine("  Starting agent …");
Console.ResetColor();

try
{
    string analysis = await agentService.AnalyzeFolderAsync(rootFolder, UserInstruction);

    PrintResultBanner();
    Console.ForegroundColor = ConsoleColor.White;
    Console.WriteLine(analysis);
    Console.ResetColor();
}
catch (Amazon.BedrockRuntime.Model.ValidationException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n  Bedrock validation error: {ex.Message}");
    Console.ResetColor();
    return 2;
}
catch (Amazon.Runtime.AmazonServiceException ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n  AWS service error ({ex.ErrorCode}): {ex.Message}");
    Console.ResetColor();
    return 3;
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.Error.WriteLine($"\n  Unexpected error: {ex.Message}");
    Console.ResetColor();
    return 4;
}

PrintFooter();
return 0;

// ─────────────────────────────────────────────────────────────────────────────
// Local helpers (top-level statement scope)
// ─────────────────────────────────────────────────────────────────────────────

static AppSettings LoadSettings()
{
    var configuration = new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
        .AddEnvironmentVariables()
        .Build();

    var settings = new AppSettings();
    configuration.Bind(settings);
    return settings;
}

static void PrintBanner(string modelId)
{
    Console.ForegroundColor = ConsoleColor.DarkCyan;
    Console.WriteLine();
    Console.WriteLine("  ╔══════════════════════════════════════════════════════════════╗");
    Console.WriteLine("  ║     Universal Folder Intelligence Agent  ·  AWS Bedrock      ║");
    string modelLine = modelId.Length > 47 ? modelId[..44] + "…" : modelId;
    Console.WriteLine($"  ║     Model: {modelLine,-47} ║");
    Console.WriteLine("  ╚══════════════════════════════════════════════════════════════╝");
    Console.ResetColor();
    Console.WriteLine();
}

static void PrintResultBanner()
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGreen;
    Console.WriteLine("  ┌─ Analysis Result ───────────────────────────────────────────┐");
    Console.ResetColor();
    Console.WriteLine();
}

static void PrintFooter()
{
    Console.WriteLine();
    Console.ForegroundColor = ConsoleColor.DarkGreen;
    Console.WriteLine("  └────────────────────────────────────────────────────────────┘");
    Console.ResetColor();
    Console.WriteLine();
}
