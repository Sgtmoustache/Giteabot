using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using Newtonsoft.Json.Linq;
using System.Collections.Generic;
using System.Linq;

class Program
{
    private DiscordSocketClient _client;
    private IServiceProvider _services;
    private string _giteaUrl;
    private string _giteaToken;
    private string _giteaOwner;
    private string _giteaRepo;

    static void Main(string[] args) => new Program().RunBotAsync().GetAwaiter().GetResult();

    public async Task RunBotAsync()
    {
        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildMessages
        };

        _client = new DiscordSocketClient(config);
        _services = new ServiceCollection()
            .AddSingleton(_client)
            .BuildServiceProvider();

        string token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");
        _giteaUrl = Environment.GetEnvironmentVariable("GITEA_URL");
        _giteaToken = Environment.GetEnvironmentVariable("GITEA_TOKEN");
        _giteaOwner = Environment.GetEnvironmentVariable("GITEA_OWNER");
        _giteaRepo = Environment.GetEnvironmentVariable("GITEA_REPO");

        _client.Log += _client_Log;
        _client.Ready += Client_Ready;
        _client.SlashCommandExecuted += SlashCommandHandler;

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private Task _client_Log(LogMessage arg)
    {
        Console.WriteLine(arg);
        return Task.CompletedTask;
    }

    public async Task Client_Ready()
    {
        var labels = await GetLabels();
        var milestones = await GetMilestones();
        var projects = await GetProjects();

        var command = new SlashCommandBuilder()
            .WithName("create_issue")
            .WithDescription("Create a new issue in Gitea")
            .AddOption("title", ApplicationCommandOptionType.String, "The issue title", isRequired: true)
            .AddOption("body", ApplicationCommandOptionType.String, "The issue body", isRequired: true)
            .AddOption("labels", ApplicationCommandOptionType.String, "Labels for the issue", isRequired: false, choices: labels.Select(l => new ApplicationCommandOptionChoiceProperties { Name = l, Value = l }).ToArray())
            .AddOption("milestone", ApplicationCommandOptionType.String, "Milestone for the issue", isRequired: false, choices: milestones.Select(m => new ApplicationCommandOptionChoiceProperties { Name = m, Value = m }).ToArray())
            .AddOption("project", ApplicationCommandOptionType.String, "Project for the issue", isRequired: false, choices: projects.Select(p => new ApplicationCommandOptionChoiceProperties { Name = p, Value = p }).ToArray());

        try
        {
            // Replace GUILD_ID with the ID of your Discord server for testing
            // For global command, use: await _client.CreateGlobalApplicationCommandAsync(command.Build());
            await _client.CreateGlobalApplicationCommandAsync(command.Build());
            Console.WriteLine("Slash command registered successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error creating slash command: {ex.Message}");
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
        var title = (string)command.Data.Options.FirstOrDefault(x => x.Name == "title")?.Value;
        var body = (string)command.Data.Options.FirstOrDefault(x => x.Name == "body")?.Value;
        var labels = (string)command.Data.Options.FirstOrDefault(x => x.Name == "labels")?.Value;
        var milestone = (string)command.Data.Options.FirstOrDefault(x => x.Name == "milestone")?.Value;
        var project = (string)command.Data.Options.FirstOrDefault(x => x.Name == "project")?.Value;

        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/issues", Method.Post);

        request.AddHeader("Authorization", $"token {_giteaToken}");
        request.AddJsonBody(new
        {
            title = title,
            body = body,
            labels = labels?.Split(',').Select(l => l.Trim()).ToArray(),
            milestone = milestone,
            project = project
        });

        var response = await client.ExecuteAsync(request);

        if (response.IsSuccessful)
        {
            var issue = JObject.Parse(response.Content);
            await command.RespondAsync($"Issue created: {issue["html_url"]}");
        }
        else
        {
            await command.RespondAsync($"Failed to create issue. Status code: {response.StatusCode}");
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

    private async Task<List<string>> GetProjects()
    {
        var client = new RestClient($"{_giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/projects");
        request.AddHeader("Authorization", $"token {_giteaToken}");

        var response = await client.ExecuteAsync(request);
        if (response.IsSuccessful)
        {
            var projects = JArray.Parse(response.Content);
            return projects.Select(p => (string)p["title"]).ToList();
        }
        return new List<string>();
    }
}