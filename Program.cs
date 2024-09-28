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

            Console.WriteLine("Retrieving projects from Gitea...");
            var projects = await GetProjects();
            Console.WriteLine($"Retrieved {projects.Count} projects");

            var command = new SlashCommandBuilder()
                .WithName("create_issue")
                .WithDescription("Create a new issue in Gitea")
                .AddOption("title", ApplicationCommandOptionType.String, "The issue title", isRequired: true)
                .AddOption("body", ApplicationCommandOptionType.String, "The issue body", isRequired: true)
                .AddOption("labels", ApplicationCommandOptionType.String, "Labels for the issue", isRequired: false,
                    choices: labels.Select(l => new ApplicationCommandOptionChoiceProperties { Name = l, Value = l }).ToArray())
                .AddOption("milestone", ApplicationCommandOptionType.String, "Milestone for the issue", isRequired: false,
                    choices: milestones.Select(m => new ApplicationCommandOptionChoiceProperties { Name = m, Value = m }).ToArray())
                .AddOption("project", ApplicationCommandOptionType.String, "Project for the issue", isRequired: false,
                    choices: projects.Select(p => new ApplicationCommandOptionChoiceProperties { Name = p.Key, Value = p.Value.ToString() }).ToArray());

            Console.WriteLine("Slash command built. Attempting to register with Discord...");
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

        string? title = command.Data.Options.FirstOrDefault(x => x.Name == "title")?.Value as string;
        string? body = command.Data.Options.FirstOrDefault(x => x.Name == "body")?.Value as string;
        string? labels = command.Data.Options.FirstOrDefault(x => x.Name == "labels")?.Value as string;
        string? milestone = command.Data.Options.FirstOrDefault(x => x.Name == "milestone")?.Value as string;
        var projectId = command.Data.Options.FirstOrDefault(x => x.Name == "project")?.Value;

        Console.WriteLine($"Received command options - Title: {title}, Labels: {labels}, Milestone: {milestone}, ProjectId: {projectId}");

        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/issues", Method.Post);

        request.AddHeader("Authorization", $"token {_giteaToken}");

        var bodyObject = new Dictionary<string, object>
    {
        { "title", title },
        { "body", body }
    };

        if (!string.IsNullOrEmpty(labels))
        {
            bodyObject["labels"] = labels.Split(',').Select(l => l.Trim()).ToArray();
        }

        if (!string.IsNullOrEmpty(milestone))
        {
            bodyObject["milestone"] = milestone;
        }

        if (projectId != null && long.TryParse((string)projectId, out long parsedProjectId))
        {
            bodyObject["project_id"] = parsedProjectId;
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

    private async Task<Dictionary<string, long>> GetProjects()
    {
        Console.WriteLine("Attempting to retrieve projects from Gitea...");
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/projects");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        Console.WriteLine($"Sending GET request to: {client.BuildUri(request)}");
        var response = await client.ExecuteAsync(request);
        Console.WriteLine($"Received response with status code: {response.StatusCode}");

        if (response.IsSuccessful)
        {
            var projects = JArray.Parse(response.Content);
            var projectDict = projects.ToDictionary(
                p => (string)p["title"],
                p => (long)p["id"]
            );
            Console.WriteLine($"Successfully retrieved {projectDict.Count} projects from Gitea");
            foreach (var project in projectDict)
            {
                Console.WriteLine($"Project: {project.Key}, ID: {project.Value}");
            }
            return projectDict;
        }
        else
        {
            Console.WriteLine($"Failed to retrieve projects. Status code: {response.StatusCode}");
            Console.WriteLine($"Error content: {response.Content}");
            Console.WriteLine($"Error message: {response.ErrorMessage}");
            return new Dictionary<string, long>();
        }
    }
}