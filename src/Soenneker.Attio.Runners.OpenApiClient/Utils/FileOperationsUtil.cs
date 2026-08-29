using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using Soenneker.Extensions.String;
using Soenneker.Git.Util.Abstract;
using Soenneker.Attio.Runners.OpenApiClient.Utils.Abstract;
using Soenneker.Utils.Dotnet.Abstract;
using Soenneker.Utils.Environment;
using Soenneker.Utils.Process.Abstract;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Soenneker.Extensions.ValueTask;
using Soenneker.Kiota.Util.Abstract;
using Soenneker.OpenApi.Fixer.Abstract;
using Soenneker.Utils.Directory.Abstract;
using Soenneker.Utils.File.Abstract;
using Soenneker.Utils.File.Download.Abstract;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Soenneker.Attio.Runners.OpenApiClient.Utils;

/// <inheritdoc cref="IFileOperationsUtil"/>
public sealed class FileOperationsUtil : IFileOperationsUtil
{
    private readonly ILogger<FileOperationsUtil> _logger;
    private readonly IConfiguration _configuration;
    private readonly IGitUtil _gitUtil;
    private readonly IDotnetUtil _dotnetUtil;
    private readonly IKiotaUtil _kiotaUtil;
    private readonly IOpenApiFixer _openApiFixer;
    private readonly IFileDownloadUtil _fileDownloadUtil;
    private readonly IFileUtil _fileUtil;
    private readonly IDirectoryUtil _directoryUtil;

    public FileOperationsUtil(ILogger<FileOperationsUtil> logger, IConfiguration configuration, IGitUtil gitUtil, IDotnetUtil dotnetUtil,
        IFileDownloadUtil fileDownloadUtil, IFileUtil fileUtil, IDirectoryUtil directoryUtil, IKiotaUtil kiotaUtil, IOpenApiFixer openApiFixer)
    {
        _logger = logger;
        _configuration = configuration;
        _gitUtil = gitUtil;
        _dotnetUtil = dotnetUtil;
        _kiotaUtil = kiotaUtil;
        _openApiFixer = openApiFixer;
        _fileDownloadUtil = fileDownloadUtil;
        _fileUtil = fileUtil;
        _directoryUtil = directoryUtil;
    }

    public async ValueTask Process(CancellationToken cancellationToken = default)
    {
        string gitDirectory = await _gitUtil.CloneToTempDirectory($"https://github.com/soenneker/{Constants.Library.ToLowerInvariantFast()}", cancellationToken: cancellationToken);

        string targetFilePath = Path.Combine(gitDirectory, "openapi.json");
        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string coreUrl = _configuration["Attio:CoreOpenApiUrl"] ?? _configuration["Attio:ClientGenerationUrl"] ?? "https://api.attio.com/openapi/api";
        string standardObjectsUrl = _configuration["Attio:StandardObjectsOpenApiUrl"] ?? "https://api.attio.com/openapi/standard-objects";
        string webhooksUrl = _configuration["Attio:WebhooksOpenApiUrl"] ?? "https://api.attio.com/openapi/webhooks";

        var sourceFilePaths = new[]
        {
            Path.Combine(gitDirectory, "openapi.core.json"),
            Path.Combine(gitDirectory, "openapi.standard-objects.json"),
            Path.Combine(gitDirectory, "openapi.webhooks.json")
        };

        try
        {
            string coreFilePath = await DownloadOpenApiDocument(coreUrl, sourceFilePaths[0], "core", cancellationToken);
            string standardObjectsFilePath = await DownloadOpenApiDocument(standardObjectsUrl, sourceFilePaths[1], "standard objects", cancellationToken);
            string webhooksFilePath = await DownloadOpenApiDocument(webhooksUrl, sourceFilePaths[2], "webhooks", cancellationToken);

            await MergeOpenApiDocuments([coreFilePath, standardObjectsFilePath, webhooksFilePath], targetFilePath, cancellationToken);
        }
        finally
        {
            foreach (string sourceFilePath in sourceFilePaths)
                await _fileUtil.DeleteIfExists(sourceFilePath, cancellationToken: cancellationToken);
        }

        string fixedFilePath = Path.Combine(gitDirectory, "openapi.fixed.json");
        await _fileUtil.DeleteIfExists(fixedFilePath, cancellationToken: cancellationToken);
        await _openApiFixer.Fix(targetFilePath, fixedFilePath, cancellationToken).NoSync();

        await _kiotaUtil.EnsureInstalled(cancellationToken);

        string srcDirectory = Path.Combine(gitDirectory, "src", Constants.Library);

        await DeleteAllExceptCsproj(srcDirectory, cancellationToken);

        await _kiotaUtil.Generate(fixedFilePath, "AttioOpenApiClient", Constants.Library, gitDirectory, cancellationToken).NoSync();

        await BuildAndPush(gitDirectory, cancellationToken).NoSync();
    }

    private async ValueTask<string> DownloadOpenApiDocument(string url, string targetFilePath, string documentName, CancellationToken cancellationToken)
    {
        await _fileUtil.DeleteIfExists(targetFilePath, cancellationToken: cancellationToken);

        string? filePath = await _fileDownloadUtil.Download(url, targetFilePath, fileExtension: ".json", cancellationToken: cancellationToken);

        if (filePath == null)
            throw new InvalidOperationException($"Attio {documentName} OpenAPI document download failed.");

        return filePath;
    }

    private static async ValueTask MergeOpenApiDocuments(IReadOnlyList<string> sourceFilePaths, string targetFilePath,
        CancellationToken cancellationToken)
    {
        JsonObject? mergedDocument = null;

        foreach (string sourceFilePath in sourceFilePaths)
        {
            string json = await File.ReadAllTextAsync(sourceFilePath, cancellationToken);
            var sourceDocument = JsonNode.Parse(json) as JsonObject ??
                                 throw new JsonException($"OpenAPI document '{sourceFilePath}' must have a JSON object at its root.");

            if (mergedDocument == null)
            {
                mergedDocument = (JsonObject) sourceDocument.DeepClone();
                continue;
            }

            MergeNamedObject(mergedDocument, sourceDocument, "paths", sourceFilePath);
            MergeNamedObject(mergedDocument, sourceDocument, "webhooks", sourceFilePath);
            MergeComponents(mergedDocument, sourceDocument, sourceFilePath);
            MergeTags(mergedDocument, sourceDocument);
        }

        if (mergedDocument == null)
            throw new InvalidOperationException("At least one OpenAPI document is required for merging.");

        await File.WriteAllTextAsync(targetFilePath, mergedDocument.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), cancellationToken);
    }

    private static void MergeComponents(JsonObject targetDocument, JsonObject sourceDocument, string sourceFilePath)
    {
        if (sourceDocument["components"] is not JsonObject sourceComponents)
            return;

        if (targetDocument["components"] is not JsonObject targetComponents)
        {
            targetDocument["components"] = sourceComponents.DeepClone();
            return;
        }

        foreach ((string componentType, JsonNode? sourceValue) in sourceComponents)
        {
            if (sourceValue is not JsonObject sourceEntries)
            {
                MergeValue(targetComponents, componentType, sourceValue, sourceFilePath);
                continue;
            }

            if (targetComponents[componentType] is not JsonObject targetEntries)
            {
                targetComponents[componentType] = sourceEntries.DeepClone();
                continue;
            }

            MergeEntries(targetEntries, sourceEntries, $"components/{componentType}", sourceFilePath);
        }
    }

    private static void MergeNamedObject(JsonObject targetDocument, JsonObject sourceDocument, string propertyName, string sourceFilePath)
    {
        if (sourceDocument[propertyName] is not JsonObject sourceEntries)
            return;

        if (targetDocument[propertyName] is not JsonObject targetEntries)
        {
            targetDocument[propertyName] = sourceEntries.DeepClone();
            return;
        }

        MergeEntries(targetEntries, sourceEntries, propertyName, sourceFilePath);
    }

    private static void MergeEntries(JsonObject target, JsonObject source, string section, string sourceFilePath)
    {
        foreach ((string name, JsonNode? value) in source)
            MergeValue(target, name, value, sourceFilePath, section);
    }

    private static void MergeValue(JsonObject target, string name, JsonNode? value, string sourceFilePath, string? section = null)
    {
        if (!target.TryGetPropertyValue(name, out JsonNode? existingValue))
        {
            target[name] = value?.DeepClone();
            return;
        }

        if (!JsonNode.DeepEquals(existingValue, value))
            throw new InvalidOperationException($"Conflicting OpenAPI definition '{section ?? "components"}/{name}' in '{sourceFilePath}'.");
    }

    private static void MergeTags(JsonObject targetDocument, JsonObject sourceDocument)
    {
        if (sourceDocument["tags"] is not JsonArray sourceTags)
            return;

        if (targetDocument["tags"] is not JsonArray targetTags)
        {
            targetDocument["tags"] = sourceTags.DeepClone();
            return;
        }

        foreach (JsonNode? sourceTag in sourceTags)
        {
            string? sourceName = sourceTag?["name"]?.GetValue<string>();
            bool exists = targetTags.Any(targetTag => targetTag?["name"]?.GetValue<string>() == sourceName);

            if (!exists)
                targetTags.Add(sourceTag?.DeepClone());
        }
    }

    /// <summary>
    /// Deletes generated files beneath the directory while preserving C# project files.
    /// </summary>
    /// <param name="directoryPath">Root directory whose generated contents should be removed.</param>
    /// <param name="cancellationToken">Token used to cancel the operation.</param>
    /// <returns>A task that completes after the targeted files have been deleted.</returns>
    public async ValueTask DeleteAllExceptCsproj(string directoryPath, CancellationToken cancellationToken = default)
    {
        if (!(await _directoryUtil.Exists(directoryPath, cancellationToken)))
        {
            _logger.LogWarning("Directory does not exist: {DirectoryPath}", directoryPath);
            return;
        }

        try
        {
            // Delete all files except .csproj
            List<string> files = await _directoryUtil.GetFilesByExtension(directoryPath, "", true, cancellationToken);
            foreach (string file in files)
            {
                if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        await _fileUtil.Delete(file, ignoreMissing: true, log: false, cancellationToken);
                        _logger.LogInformation("Deleted file: {FilePath}", file);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to delete file: {FilePath}", file);
                    }
                }
            }

            // Delete all empty subdirectories
            List<string> dirs = await _directoryUtil.GetAllDirectoriesRecursively(directoryPath, cancellationToken);
            foreach (string dir in dirs.OrderByDescending(d => d.Length)) // Sort by depth to delete from deepest first
            {
                try
                {
                    List<string> dirFiles = await _directoryUtil.GetFilesByExtension(dir, "", false, cancellationToken);
                    List<string> subDirs = await _directoryUtil.GetAllDirectories(dir, cancellationToken);
                    if (dirFiles.Count == 0 && subDirs.Count == 0)
                    {
                        await _directoryUtil.Delete(dir, cancellationToken);
                        _logger.LogInformation("Deleted empty directory: {DirectoryPath}", dir);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to delete directory: {DirectoryPath}", dir);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "An error occurred while cleaning the directory: {DirectoryPath}", directoryPath);
        }
    }

    private async ValueTask BuildAndPush(string gitDirectory, CancellationToken cancellationToken)
    {
        string projFilePath = Path.Combine(gitDirectory, "src", Constants.Library, $"{Constants.Library}.csproj");

        await _dotnetUtil.Restore(projFilePath, cancellationToken: cancellationToken);

        bool successful = await _dotnetUtil.Build(projFilePath, true, "Release", false, cancellationToken: cancellationToken);

        if (!successful)
        {
            _logger.LogError("Build was not successful, exiting...");
            return;
        }

        string gitHubToken = EnvironmentUtil.GetVariableStrict("GH__TOKEN");
        string name = EnvironmentUtil.GetVariableStrict("GIT__NAME");
        string email = EnvironmentUtil.GetVariableStrict("GIT__EMAIL");

        await _gitUtil.CommitAndPush(gitDirectory, "Automated update", gitHubToken, name, email, cancellationToken);
    }
}
