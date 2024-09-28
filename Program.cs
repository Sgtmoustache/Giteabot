using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using Newtonsoft.Json.Linq;

class Program
{
    private DiscordSocketClient _client;
    private IServiceProvider _services;
    private string _giteaUrl;
    private string _giteaToken;
    private string _giteaOwner;
    private string _giteaRepo;
    private long? _defaultProjectId;

    static void Main(string[] args)
    {
        Console.WriteLine("Starting bot...");
        new Program().RunBotAsync().GetAwaiter().GetResult();
    }

    public async Task RunBotAsync()
    {
        Console.WriteLine("Initializing bot...");
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
        };

        _client = new DiscordSocketClient(config);
        _services = new ServiceCollection()
            .AddSingleton(_client)
            .BuildServiceProvider();

        Console.WriteLine("Reading environment variables...");
        string token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        _giteaUrl = Environment.GetEnvironmentVariable("GITEA_URL");
        _giteaToken = Environment.GetEnvironmentVariable("GITEA_TOKEN");
        _giteaOwner = Environment.GetEnvironmentVariable("GITEA_OWNER");
        _giteaRepo = Environment.GetEnvironmentVariable("GITEA_REPO");
        string defaultProjectIdString = Environment.GetEnvironmentVariable("DEFAULT_PROJECT_ID");

        if (!string.IsNullOrEmpty(defaultProjectIdString) && long.TryParse(defaultProjectIdString, out long projectId))
        {
            _defaultProjectId = projectId;
            Console.WriteLine($"Default Project ID set: {_defaultProjectId}");
        }
        else
        {
            Console.WriteLine("No valid Default Project ID set");
        }

        Console.WriteLine($"Gitea URL: {_giteaUrl}");
        Console.WriteLine($"Gitea Owner: {_giteaOwner}");
        Console.WriteLine($"Gitea Repo: {_giteaRepo}");
        Console.WriteLine($"Discord Token length: {token?.Length ?? 0}");
        Console.WriteLine($"Gitea Token length: {_giteaToken?.Length ?? 0}");

        _client.Log += _client_Log;
        _client.Ready += Client_Ready;
        _client.SlashCommandExecuted += SlashCommandHandler;

        Console.WriteLine("Logging in to Discord...");
        await _client.LoginAsync(TokenType.Bot, token);
        Console.WriteLine("Starting Discord client...");
        await _client.StartAsync();

        Console.WriteLine("Bot is now running. Press Ctrl+C to exit.");
        await Task.Delay(-1);
    }
    private Task _client_Log(LogMessage arg)
    {
        Console.WriteLine($"Discord.Net: {arg}");
        return Task.CompletedTask;
    }

    public async Task Client_Ready()
    {
        Console.WriteLine("Client is ready. Initializing slash commands...");
        try
        {
            Console.WriteLine("Retrieving labels from Gitea...");
            var labels = await GetLabels();
            Console.WriteLine($"Retrieved {labels.Count} labels");

            Console.WriteLine("Retrieving milestones from Gitea...");
            var milestones = await GetMilestones();
            Console.WriteLine($"Retrieved {milestones.Count} milestones");

            var command = new SlashCommandBuilder()
                .WithName("create_issue")
                .WithDescription("Create a new issue in Gitea")
                .AddOption("title", ApplicationCommandOptionType.String, "The issue title", isRequired: true)
                .AddOption("body", ApplicationCommandOptionType.String, "The issue body", isRequired: true)
                .AddOption("labels", ApplicationCommandOptionType.String, "Labels for the issue", isRequired: false,
                    choices: labels.Select(l => new ApplicationCommandOptionChoiceProperties { Name = l, Value = l }).ToArray())
                .AddOption("milestone", ApplicationCommandOptionType.String, "Milestone for the issue", isRequired: false,
                    choices: milestones.Select(m => new ApplicationCommandOptionChoiceProperties { Name = m, Value = m }).ToArray());

            Console.WriteLine("Slash command built. Attempting to register with Discord...");
            // Replace GUILD_ID with the ID of your Discord server for testing
            // For global command, use: await _client.CreateGlobalApplicationCommandAsync(command.Build());
            var guildCommand = await _client.CreateGlobalApplicationCommandAsync(command.Build());
            Console.WriteLine($"Slash command registered successfully. Command ID: {guildCommand.Id}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Client_Ready: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        if (command.Data.Name == "create_issue")
        {
            await HandleCreateIssueCommand(command);
        }
    }

    private async Task HandleCreateIssueCommand(SocketSlashCommand command)
    {
        Console.WriteLine($"Handling create issue command from user: {command.User.Username}");

        var title = (string)command.Data.Options.FirstOrDefault(x => x.Name == "title")?.Value;
        var body = (string)command.Data.Options.FirstOrDefault(x => x.Name == "body")?.Value;
        var labelNames = (string)command.Data.Options.FirstOrDefault(x => x.Name == "labels")?.Value;
        var milestone = (string)command.Data.Options.FirstOrDefault(x => x.Name == "milestone")?.Value;

        Console.WriteLine($"Received command options - Title: {title}, Labels: {labelNames}, Milestone: {milestone}");

        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/issues", Method.Post);

        request.AddHeader("Authorization", $"token {_giteaToken}");

        var bodyObject = new Dictionary<string, object>
    {
        { "title", title },
        { "body", body }
    };

        if (!string.IsNullOrEmpty(labelNames))
        {
            var labelIds = await GetLabelIds(labelNames.Split(',').Select(l => l.Trim()).ToArray());
            if (labelIds.Any())
            {
                bodyObject["labels"] = labelIds;
            }
        }

        if (!string.IsNullOrEmpty(milestone))
        {
            var milestoneId = await GetMilestoneId(milestone);
            if (milestoneId.HasValue)
            {
                bodyObject["milestone"] = milestoneId.Value;
            }
        }

        if (_defaultProjectId.HasValue)
        {
            bodyObject["project_id"] = _defaultProjectId.Value;
            Console.WriteLine($"Using default project ID: {_defaultProjectId.Value}");
        }
        else
        {
            Console.WriteLine("No default project ID set");
        }

        request.AddJsonBody(bodyObject);

        Console.WriteLine($"Sending POST request to: {client.BuildUri(request)}");
        Console.WriteLine($"Request body: {System.Text.Json.JsonSerializer.Serialize(bodyObject)}");

        var response = await client.ExecuteAsync(request);

        Console.WriteLine($"Received response with status code: {response.StatusCode}");

        if (response.IsSuccessful)
        {
            var issue = JObject.Parse(response.Content);
            Console.WriteLine($"Successfully created issue. URL: {issue["html_url"]}");
            await command.RespondAsync($"Issue created: {issue["html_url"]}");
        }
        else
        {
            Console.WriteLine($"Error creating issue. Status code: {response.StatusCode}");
            Console.WriteLine($"Error content: {response.Content}");
            Console.WriteLine($"Error message: {response.ErrorMessage}");
            await command.RespondAsync($"Failed to create issue. Status code: {response.StatusCode}. Error: {response.Content}");
        }
    }

    private async Task<List<long>> GetLabelIds(string[] labelNames)
    {
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/labels");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        var response = await client.ExecuteAsync(request);
        if (response.IsSuccessful)
        {
            var labels = JArray.Parse(response.Content);
            return labels
                .Where(l => labelNames.Contains((string)l["name"]))
                .Select(l => (long)l["id"])
                .ToList();
        }

        Console.WriteLine($"Failed to retrieve label IDs. Status code: {response.StatusCode}");
        return new List<long>();
    }

    private async Task<long?> GetMilestoneId(string milestoneName)
    {
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/milestones");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        var response = await client.ExecuteAsync(request);
        if (response.IsSuccessful)
        {
            var milestones = JArray.Parse(response.Content);
            var milestone = milestones.FirstOrDefault(m => (string)m["title"] == milestoneName);
            return milestone != null ? (long?)milestone["id"] : null;
        }

        Console.WriteLine($"Failed to retrieve milestone ID. Status code: {response.StatusCode}");
        return null;
    }

    private async Task<List<string>> GetLabels()
    {
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/labels");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        var response = await client.ExecuteAsync(request);
        if (response.IsSuccessful)
        {
            var labels = JArray.Parse(response.Content);
            return labels.Select(l => (string)l["name"]).ToList();
        }
        return new List<string>();
    }

    private async Task<List<string>> GetMilestones()
    {
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/milestones");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        var response = await client.ExecuteAsync(request);
        if (response.IsSuccessful)
        {
            var milestones = JArray.Parse(response.Content);
            return milestones.Select(m => (string)m["title"]).ToList();
        }
        return new List<string>();
    }
}