using System;
using System.Threading.Tasks;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using Newtonsoft.Json.Linq;

class Program
{
    private DiscordSocketClient _client;
    private CommandService _commands;
    private IServiceProvider _services;

    static void Main(string[] args) => new Program().RunBotAsync().GetAwaiter().GetResult();

    public async Task RunBotAsync()
    {
        _client = new DiscordSocketClient();
        _commands = new CommandService();
        _services = new ServiceCollection()
            .AddSingleton(_client)
            .AddSingleton(_commands)
            .BuildServiceProvider();

        string token = Environment.GetEnvironmentVariable("DISCORD_TOKEN");

        _client.Log += _client_Log;

        await RegisterCommandsAsync();

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();

        await Task.Delay(-1);
    }

    private Task _client_Log(LogMessage arg)
    {
        Console.WriteLine(arg);
        return Task.CompletedTask;
    }

    public async Task RegisterCommandsAsync()
    {
        _client.MessageReceived += HandleCommandAsync;
        await _commands.AddModulesAsync(System.Reflection.Assembly.GetEntryAssembly(), _services);
    }

    private async Task HandleCommandAsync(SocketMessage arg)
    {
        var message = arg as SocketUserMessage;
        var context = new SocketCommandContext(_client, message);
        if (message.Author.IsBot) return;

        int argPos = 0;
        if (message.HasStringPrefix("!", ref argPos))
        {
            var result = await _commands.ExecuteAsync(context, argPos, _services);
            if (!result.IsSuccess) Console.WriteLine(result.ErrorReason);
        }
    }
}

public class Commands : ModuleBase<SocketCommandContext>
{
    [Command("create_issue")]
    public async Task CreateIssue(string repo, string title, [Remainder] string body)
    {
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
            await ReplyAsync($"Issue created: {issue["html_url"]}");
        }
        else
        {
            await ReplyAsync($"Failed to create issue. Status code: {response.StatusCode}");
        }
    }
}