using System;
using System.Threading.Tasks;
using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using Newtonsoft.Json.Linq;

class Program
{
    private DiscordSocketClient _client;
    private IServiceProvider _services;

    static void Main(string[] args) => new Program().RunBotAsync().GetAwaiter().GetResult();

    public async Task RunBotAsync()
    {
        _client = new DiscordSocketClient();
        _services = new ServiceCollection()
            .AddSingleton(_client)
            .BuildServiceProvider();

        string token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

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
        var globalCommand = new SlashCommandBuilder()
            .WithName("create_issue")
            .WithDescription("Create a new issue in Gitea")
            .AddOption("repo", ApplicationCommandOptionType.String, "The repository name", isRequired: true)
            .AddOption("title", ApplicationCommandOptionType.String, "The issue title", isRequired: true)
            .AddOption("body", ApplicationCommandOptionType.String, "The issue body", isRequired: true);

        try
        {
            await _client.CreateGlobalApplicationCommandAsync(globalCommand.Build());
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
        var repo = (string)command.Data.Options.FirstOrDefault(x => x.Name == "repo")?.Value;
        var title = (string)command.Data.Options.FirstOrDefault(x => x.Name == "title")?.Value;
        var body = (string)command.Data.Options.FirstOrDefault(x => x.Name == "body")?.Value;

        string giteaUrl = Environment.GetEnvironmentVariable("GITEA_URL");
        string giteaToken = Environment.GetEnvironmentVariable("GITEA_TOKEN");
        string giteaOwner = Environment.GetEnvironmentVariable("GITEA_OWNER");

        var client = new RestClient($"{giteaUrl}/api/v1");
        var request = new RestRequest($"repos/{giteaOwner}/{repo}/issues", Method.Post);

        request.AddHeader("Authorization", $"token {giteaToken}");
        request.AddJsonBody(new { title = title, body = body });

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
}