using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UnturnedModdingCollective.Interactions.Commands;
public class UnityVersionCommand : InteractionModuleBase<SocketInteractionContext<SocketSlashCommand>>
{
    private static readonly Regex ParseVersionRegex = new Regex(@"\d+\.\d+\.\d+\.\d+", RegexOptions.CultureInvariant | RegexOptions.Singleline | RegexOptions.Compiled);
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<UnityVersionCommand> _logger;
    public UnityVersionCommand(HttpClient httpClient, IConfiguration configuration, ILogger<UnityVersionCommand> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration.GetSection("UnityVersion");
    }

    [SlashCommand("unity-version", "Get the current unity version of Unturned. Inspired by UO.")]
    public Task UnityVersion([Name("preview-branch")] bool previewBranch = false, [Name("unturned-version")] string? unturnedVersion = null)
    {
        return !string.IsNullOrEmpty(unturnedVersion)
               ? SearchForExistingVersion(unturnedVersion)
               : SearchForLatestVersion(previewBranch);
    }

    /// <summary>
    /// Parse a <see cref="Version"/> from a commit message looking somewhat like the following: <c>DD Month YYYY - Version X.X.X.X (XXXXXXXX)</c>.
    /// </summary>
    private static bool TryParseVersion(string message, [MaybeNullWhen(false)] out Version unturnedVersion)
    {
        unturnedVersion = null;
        Match match = ParseVersionRegex.Match(message);
        return match.Success && Version.TryParse(match.ValueSpan, out unturnedVersion);
    }

    /// <summary>
    /// Queries for the latest commits to Unturned-Datamining to find the latest preview or release version.
    /// </summary>
    private async Task SearchForLatestVersion(bool previewBranch)
    {
        await Context.Interaction.DeferAsync();

        string? uri = _configuration["CommitsEndpoint"];
        if (string.IsNullOrEmpty(uri))
        {
            await GetLatestPreviewVersionBackup();
            return;
        }

        JsonDocument doc;
        try
        {
            // download most recent commits
            HttpResponseMessage response = await CreateGitHubApiRequest(uri);
            if (!response.IsSuccessStatusCode)
            {
                await GetLatestPreviewVersionBackup();
                return;
            }

            await using Stream stream = await response.Content.ReadAsStreamAsync();

            doc = JsonDocument.Parse(stream, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 5
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to download commit history for Unturned-Datamining.");
            await GetLatestPreviewVersionBackup();
            return;
        }

        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Array)
        {
            await GetLatestPreviewVersionBackup();
            return;
        }

        // read commits from github API. looking for: [ { sha: "commit id target", commit: { message: "message target" } } ]
        // continue until a version matches the preview version argument
        Version? version = null;
        string? commitId = null;
        foreach (JsonElement objectElement in root.EnumerateArray())
        {
            if (objectElement.ValueKind != JsonValueKind.Object)
                break;

            if (!objectElement.TryGetProperty("sha", out JsonElement commitIdElement)
                || commitIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            if (!objectElement.TryGetProperty("commit", out JsonElement commitObject)
                || commitObject.ValueKind != JsonValueKind.Object
                || !commitObject.TryGetProperty("message", out JsonElement messageElement)
                || messageElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            string? message = messageElement.GetString();
            commitId = commitIdElement.GetString();

            if (message == null || commitId == null || !TryParseVersion(message, out version))
            {
                continue;
            }

            // Skips preview updates. Nelson usually uses a 1XX number for the revision.
            if (previewBranch || version.Revision < 100)
                break;
        }

        if (version == null)
        {
            await GetLatestPreviewVersionBackup();
            return;
        }

        await GetSpecificVersion(version, commitId!);
    }

    /// <summary>
    /// Use the GitHub API to search for a version number in the commit history of Unturned-Datamining.
    /// </summary>
    private async Task SearchForExistingVersion(string unturnedVersion)
    {
        if (!Version.TryParse((unturnedVersion.StartsWith('v') ? unturnedVersion.AsSpan(1) : unturnedVersion.AsSpan()).Trim(), out Version? specificVersion)
            || specificVersion.Major != 3
            || specificVersion.Minor > 255
            || specificVersion.Build > 255
            || specificVersion.Revision > 255
           )
        {
            await Context.Interaction.RespondAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Invalid Version")
                .WithDescription("All Unturned versions are in the format: `3.0.0.0`.")
                .Build());

            return;
        }

        await Context.Interaction.DeferAsync();

        string? uri = _configuration["SearchVersionEndpoint"];
        if (string.IsNullOrEmpty(uri))
        {
            await Context.Interaction.FollowupAsync("Missing \"SearchVersionEndpoint\" URI in config.");
            return;
        }

        uri = string.Format(uri, specificVersion.ToString(4));

        Stream? rawStream = null;

        try
        {
            HttpResponseMessage response = await CreateGitHubApiRequest(uri);

            if (response.IsSuccessStatusCode)
                rawStream = await response.Content.ReadAsStreamAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error searching for Unturned Version.");
        }

        JsonElement root = default;
        if (rawStream != null)
        {
            try
            {
                JsonDocument doc = JsonDocument.Parse(rawStream, new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                    MaxDepth = 5
                });

                root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    root = default;
                }
                else if (!root.TryGetProperty("items", out JsonElement itemsElement) || itemsElement.ValueKind != JsonValueKind.Array)
                {
                    root = default;
                }
                else
                {
                    root = itemsElement;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error parsing search results for Unturned version.");
            }
            finally
            {
                await rawStream.DisposeAsync();
            }
        }

        if (root.ValueKind == JsonValueKind.Undefined)
        {
            await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Error")
                .WithDescription("Web request to the GitHub API failed. Try again later.")
                .Build());

            return;
        }

        if (root.GetArrayLength() == 0)
        {
            await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Unknown Version")
                .WithDescription($"Unturned `v{specificVersion}` is not on record or doesn't exist.")
                .Build());

            return;
        }

        JsonElement firstElement = root[0];

        if (firstElement.ValueKind != JsonValueKind.Object
            || !firstElement.TryGetProperty("sha", out JsonElement commitIdElement)
            || commitIdElement.ValueKind != JsonValueKind.String
            || commitIdElement.GetString() is not { Length: > 0 } commitId)
        {
            await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                .WithColor(Color.Red)
                .WithTitle("Error")
                .WithDescription("Web request to the GitHub API failed. Try again later.")
                .Build());

            return;
        }

        await GetSpecificVersion(specificVersion, commitId);
    }

    /// <summary>
    /// Query the specified commit's version of the '.unityversion' file.
    /// </summary>
    private async Task GetSpecificVersion(Version unturnedVersion, string commitId)
    {
        string? uri = _configuration["OtherCommitEndpoint"];
        if (string.IsNullOrEmpty(uri))
        {
            await Context.Interaction.FollowupAsync("Missing \"OtherCommitEndpoint\" URI in config.");
            return;
        }

        string? version = await TryDownloadRawUnityVersion(string.Format(uri, commitId), unturnedVersion);

        if (version == null)
            return;

        EmbedBuilder embed = new EmbedBuilder();

        if (unturnedVersion.Revision >= 100)
            embed.Description = $"The Unity version for Unturned **preview** `v{unturnedVersion.ToString(4)}` is `{version}`.";
        else
            embed.Description = $"The Unity version for Unturned `v{unturnedVersion.ToString(4)}` is `{version}`.";

        ComponentBuilder components = new ComponentBuilder();
        SetupMessage(embed, components, version);

        await Context.Interaction.FollowupAsync(embed: embed.Build(), components: components.Build());
    }

    /// <summary>
    /// Back-up function to directly query the latest version of the '.unityversion' file instead of a specific version from a commit.
    /// </summary>
    private async Task GetLatestPreviewVersionBackup()
    {
        string? uri = _configuration["LatestCommitEndpoint"];
        if (string.IsNullOrEmpty(uri))
        {
            await Context.Interaction.FollowupAsync("Missing \"LatestCommitEndpoint\" URI in config.");
            return;
        }

        string? version = await TryDownloadRawUnityVersion(uri, null);

        if (version == null)
            return;

        EmbedBuilder embed = new EmbedBuilder()
            .WithDescription($"The current Unity version is `{version}`.");

        ComponentBuilder components = new ComponentBuilder();
        SetupMessage(embed, components, version);

        await Context.Interaction.FollowupAsync(embed: embed.Build(), components: components.Build());
    }
    private async Task<string?> TryDownloadRawUnityVersion(string uri, Version? specificVersion)
    {
        try
        {
            HttpResponseMessage response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseContentRead);

            if (response.IsSuccessStatusCode)
                return await response.Content.ReadAsStringAsync();
            
            if (response.StatusCode == HttpStatusCode.NotFound && specificVersion != null)
            {
                await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
                    .WithColor(Color.Red)
                    .WithTitle("Unknown Version")
                    .WithDescription($"Unity version was not tracked when `v{specificVersion.ToString(4)}` was released.")
                    .Build());

                return null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error downloading Unity Version.");
        }

        await Context.Interaction.FollowupAsync(embed: new EmbedBuilder()
            .WithColor(Color.Red)
            .WithTitle("Error")
            .WithDescription($"Web request to [Unturned-Datamining/.unityVersion]({uri}) failed. Try again later.")
            .Build());

        return null;
    }
    private Task<HttpResponseMessage> CreateGitHubApiRequest(string uri)
    {
        HttpRequestMessage message = new HttpRequestMessage(HttpMethod.Get, uri);

        // GitHub API requires User-Agent header.
        message.Headers.Add("User-Agent", $"UnturnedModdingCollective.Bot v{Assembly.GetExecutingAssembly().GetName().Version!.ToString(3)}");

        return _httpClient.SendAsync(message, HttpCompletionOption.ResponseContentRead);
    }
    private static void SetupMessage(EmbedBuilder embed, ComponentBuilder components, string version)
    {
        string versionNoFSuffix = version;
        int fInd = versionNoFSuffix.IndexOf('f');
        if (fInd != -1)
            versionNoFSuffix = versionNoFSuffix[..fInd];

        components.WithButton("Download Archive", style: ButtonStyle.Link, url: "https://unity.com/releases/editor/archive", row: 0)
                  .WithButton("Unity Hub", style: ButtonStyle.Link, url: "https://unity.com/unity-hub#stay-tuned-next-evolution--2", row: 0)
                  .WithButton(versionNoFSuffix, style: ButtonStyle.Link, url: $"https://unity.com/releases/editor/whats-new/{versionNoFSuffix}", row: 1);

        embed.WithAuthor("Unturned-Datamining", "https://avatars.githubusercontent.com/u/118498467", "https://github.com/Unturned-Datamining/Unturned-Datamining")
             .AddField("Unity Hub", $"Paste in browser or **Win + R**:{Environment.NewLine}__`unityhub://{version}/`__")
             .WithColor(new Color(99, 123, 101));

        embed.Timestamp = DateTimeOffset.UtcNow;
    }
}
