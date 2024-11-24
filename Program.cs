using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using RestSharp;
using Newtonsoft.Json.Linq;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;

class Program
{
    private DiscordSocketClient _client;
    private IServiceProvider _services;
    private string _giteaUrl;
    private string _giteaToken;
    private string _giteaOwner;
    private string _giteaRepo;
    private long? _defaultProjectId;
    private ulong _updateChannelId;
    private string _webhookUrl;
    private int _webhookPort;

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
        _updateChannelId = ulong.Parse(Environment.GetEnvironmentVariable("UPDATE_CHANNEL_ID"));
        //_webhookUrl = Environment.GetEnvironmentVariable("WEBHOOK_URL") ?? "http://0.0.0.0";
        //_webhookPort = int.Parse(Environment.GetEnvironmentVariable("WEBHOOK_PORT") ?? "3000");

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
        Console.WriteLine($"Update Channel ID: {_updateChannelId}");
        Console.WriteLine($"Webhook URL: {_webhookUrl}");
        Console.WriteLine($"Webhook Port: {_webhookPort}");

        _client.Log += _client_Log;
        _client.Ready += Client_Ready;
        _client.SlashCommandExecuted += SlashCommandHandler;

        Console.WriteLine("Logging in to Discord...");
        await _client.LoginAsync(TokenType.Bot, token);
        Console.WriteLine("Starting Discord client...");
        await _client.StartAsync();


        //DOES NOT WORK
        //Console.WriteLine("Starting webhook listener...");
        //await StartWebhookListener();

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

            var createIssueCommand = new SlashCommandBuilder()
                .WithName("create_issue")
                .WithDescription("Create a new issue in Gitea")
                .AddOption("title", ApplicationCommandOptionType.String, "The issue title", isRequired: true)
                .AddOption("body", ApplicationCommandOptionType.String, "The issue body", isRequired: true)
                .AddOption("labels", ApplicationCommandOptionType.String, "Labels for the issue", isRequired: false,
                    choices: labels.Select(l => new ApplicationCommandOptionChoiceProperties { Name = l, Value = l })
                        .ToArray())
                .AddOption("milestone", ApplicationCommandOptionType.String, "Milestone for the issue",
                    isRequired: false,
                    choices: milestones
                        .Select(m => new ApplicationCommandOptionChoiceProperties { Name = m, Value = m }).ToArray());

            var listCommand = new SlashCommandBuilder()
                .WithName("list_issues")
                .WithDescription("List issues from Gitea with optional filters")
                .AddOption("status", ApplicationCommandOptionType.String, "Filter by issue status", isRequired: false,
                    choices: new[]
                    {
                        new ApplicationCommandOptionChoiceProperties { Name = "Open", Value = "open" },
                        new ApplicationCommandOptionChoiceProperties { Name = "Closed", Value = "closed" }
                    })
                .AddOption("labels", ApplicationCommandOptionType.String, "Filter by labels (comma-separated)",
                    isRequired: false,
                    choices: labels.Select(l => new ApplicationCommandOptionChoiceProperties { Name = l, Value = l })
                        .ToArray())
                .AddOption("milestone", ApplicationCommandOptionType.String, "Filter by milestone", isRequired: false,
                    choices: milestones
                        .Select(m => new ApplicationCommandOptionChoiceProperties { Name = m, Value = m }).ToArray());

            await _client.CreateGlobalApplicationCommandAsync(createIssueCommand.Build());
            await _client.CreateGlobalApplicationCommandAsync(listCommand.Build());
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in Client_Ready: {ex.Message}");
            Console.WriteLine($"Stack trace: {ex.StackTrace}");
        }
    }

    private async Task SlashCommandHandler(SocketSlashCommand command)
    {
        try
        {
            // Defer immediately for any command that might take time
            await command.DeferAsync();

            if (command.Data.Name == "create_issue")
            {
                await HandleCreateIssueCommand(command);
            }
            else if (command.Data.Name == "list_issues")
            {
                await HandleListIssuesCommand(command);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error handling command {command.Data.Name}: {ex.Message}");
            try
            {
                // Try to send an error message if we haven't responded yet
                if (!command.HasResponded)
                {
                    await command.RespondAsync($"An error occurred while processing the command: {ex.Message}", ephemeral: true);
                }
                else
                {
                    await command.ModifyOriginalResponseAsync(msg => msg.Content = $"An error occurred while processing the command: {ex.Message}");
                }
            }
            catch (Exception messageEx)
            {
                Console.WriteLine($"Failed to send error message: {messageEx.Message}");
            }
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

            var embed = new EmbedBuilder()
                .WithTitle($"New Issue Created: {title}")
                .WithUrl((string)issue["html_url"])
                .WithColor(Color.Green)
                .AddField("Title", title, true)
                .AddField("Body", body ?? "No description", true)
                .AddField("Labels", labelNames ?? "None", true)
                .AddField("Milestone", milestone ?? "None", true)
                .AddField("Project ID", _defaultProjectId?.ToString() ?? "None", true)
                .WithFooter($"Created by {command.User.Username}")
                .Build();

            await command.RespondAsync(embed: embed);
        }
        else
        {
            Console.WriteLine($"Error creating issue. Status code: {response.StatusCode}");
            Console.WriteLine($"Error content: {response.Content}");
            Console.WriteLine($"Error message: {response.ErrorMessage}");
            await command.RespondAsync(
                $"Failed to create issue. Status code: {response.StatusCode}. Error: {response.Content}");
        }
    }

    private async Task HandleListIssuesCommand(SocketSlashCommand command)
{
    var status = (string)command.Data.Options.FirstOrDefault(x => x.Name == "status")?.Value ?? "open";
    var labelNames = (string)command.Data.Options.FirstOrDefault(x => x.Name == "labels")?.Value;
    var milestone = (string)command.Data.Options.FirstOrDefault(x => x.Name == "milestone")?.Value;

    Console.WriteLine($"Listing issues with filters - Status: {status}, Labels: {labelNames}, Milestone: {milestone}");

    var client = new RestClient($"{_giteaUrl}/api/v1");
    var request = new RestRequest($"repos/{_giteaOwner}/{_giteaRepo}/issues");
    request.AddHeader("Authorization", $"token {_giteaToken}");
    
    request.AddQueryParameter("state", status);
    if (!string.IsNullOrEmpty(labelNames))
    {
        request.AddQueryParameter("labels", labelNames);
    }
    if (!string.IsNullOrEmpty(milestone))
    {
        var milestoneId = await GetMilestoneId(milestone);
        if (milestoneId.HasValue)
        {
            request.AddQueryParameter("milestone", milestoneId.Value.ToString());
        }
    }

    var response = await client.ExecuteAsync(request);

    if (response.IsSuccessful)
    {
        var issues = JArray.Parse(response.Content);
        
        if (!issues.Any())
        {
            await command.ModifyOriginalResponseAsync(msg => 
                msg.Content = "No issues found matching the specified criteria.");
            return;
        }

        // Safely group issues by milestone and labels
        var groupedIssues = issues
            .GroupBy(issue => 
            {
                var milestoneToken = issue["milestone"];
                return milestoneToken != null && milestoneToken.Type != JTokenType.Null 
                    ? milestoneToken["title"]?.ToString() ?? "No Milestone"
                    : "No Milestone";
            })
            .OrderBy(group => group.Key == "No Milestone")
            .ThenBy(group => group.Key)
            .ToDictionary(
                group => group.Key,
                group => group.GroupBy(issue => 
                    {
                        var labelsToken = issue["labels"];
                        if (labelsToken == null || labelsToken.Type == JTokenType.Null || !labelsToken.Any())
                        {
                            return "No Labels";
                        }

                        var labels = labelsToken
                            .Select(l => l["name"]?.ToString())
                            .Where(l => !string.IsNullOrEmpty(l))
                            .OrderBy(l => l);

                        return labels.Any() ? string.Join(", ", labels) : "No Labels";
                    })
                    .OrderBy(g => g.Key == "No Labels")
                    .ThenBy(g => g.Key)
                    .ToDictionary(g => g.Key, g => g.ToList())
            );
        
        var embedBuilder = new EmbedBuilder()
            .WithTitle($"Issues List ({status})")
            .WithColor(status == "open" ? Color.Green : Color.Red)
            .WithFooter($"Total: {issues.Count} issues");

        var description = new System.Text.StringBuilder();
        bool reachedLimit = false;

        foreach (var milestoneGroup in groupedIssues)
        {
            if (milestone is not null && milestoneGroup.Key != milestone)
                continue;
            
            description.AppendLine($"__**{milestoneGroup.Key}**__");
            
            foreach (var labelGroup in milestoneGroup.Value)
            {
                description.AppendLine($"\n**{labelGroup.Key}**");
                
                foreach (var issue in labelGroup.Value)
                {
                    // Safely access issue properties
                    var issueNumber = issue["number"]?.ToObject<int>() ?? 0;
                    var issueTitle = issue["title"]?.ToString() ?? "Untitled";
                    var issueUrl = issue["html_url"]?.ToString() ?? "";
                    
                    if (issueNumber != 0 && !string.IsNullOrEmpty(issueUrl))
                    {
                        var line = $"#{issueNumber} [{issueTitle}]({issueUrl})\n";
                        
                        if (description.Length + line.Length > 4000)
                        {
                            description.AppendLine("\n... (too many issues to display all)");
                            reachedLimit = true;
                            break;
                        }
                        
                        description.Append(line);
                    }
                }
                
                if (reachedLimit) break;
            }
            
            if (reachedLimit) break;
            description.AppendLine();
        }

        embedBuilder.WithDescription(description.ToString());
        await command.ModifyOriginalResponseAsync(msg => msg.Embed = embedBuilder.Build());
    }
    else
    {
        Console.WriteLine($"Error listing issues. Status code: {response.StatusCode}");
        Console.WriteLine($"Error content: {response.Content}");
        await command.ModifyOriginalResponseAsync(msg => 
            msg.Content = $"Failed to list issues. Status code: {response.StatusCode}");
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

    private async Task StartWebhookListener()
    {
        await Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.Configure(app =>
                    {
                        app.UseRouting();
                        app.UseEndpoints(endpoints =>
                        {
                            endpoints.MapPost("/webhook", async context =>
                            {
                                using var reader = new StreamReader(context.Request.Body);
                                var body = await reader.ReadToEndAsync();
                                await HandleWebhook(body, context.Request.Headers["X-Gitea-Event"]);
                                await context.Response.WriteAsync("Webhook received");
                            });
                        });
                    })
                    .UseUrls($"{_webhookUrl}:{_webhookPort}");
            })
            .Build()
            .RunAsync();
    }

    private async Task HandleWebhook(string payload, string eventType)
    {
        var json = JObject.Parse(payload);
        var channel = _client.GetChannel(_updateChannelId) as IMessageChannel;

        if (channel == null)
        {
            Console.WriteLine($"Error: Channel with ID {_updateChannelId} not found");
            return;
        }

        EmbedBuilder embed = new EmbedBuilder();

        switch (eventType)
        {
            case "pull_request":
                var action = json["action"].ToString();
                var prTitle = json["pull_request"]["title"].ToString();
                var prUrl = json["pull_request"]["html_url"].ToString();
                var prUser = json["pull_request"]["user"]["username"].ToString();

                if (action == "opened")
                {
                    embed.WithTitle("New Merge Request")
                        .WithDescription($"[{prTitle}]({prUrl})")
                        .WithColor(Color.Blue)
                        .AddField("Created by", prUser);
                }
                else if (action == "closed" && json["pull_request"]["merged"].ToObject<bool>())
                {
                    embed.WithTitle("Merge Request Merged")
                        .WithDescription($"[{prTitle}]({prUrl})")
                        .WithColor(Color.Green)
                        .AddField("Merged by", prUser);
                }

                break;

            case "issues":
                action = json["action"].ToString();
                var issueTitle = json["issue"]["title"].ToString();
                var issueUrl = json["issue"]["html_url"].ToString();
                var issueUser = json["issue"]["user"]["username"].ToString();

                if (action == "opened")
                {
                    embed.WithTitle("New Issue")
                        .WithDescription($"[{issueTitle}]({issueUrl})")
                        .WithColor(Color.Orange)
                        .AddField("Created by", issueUser);
                }
                else if (action == "closed")
                {
                    embed.WithTitle("Issue Closed")
                        .WithDescription($"[{issueTitle}]({issueUrl})")
                        .WithColor(Color.Red)
                        .AddField("Closed by", issueUser);
                }

                break;

            default:
                Console.WriteLine($"Unhandled event type: {eventType}");
                return;
        }

        if (embed.Fields.Count > 0)
        {
            await channel.SendMessageAsync(embed: embed.Build());
        }
    }
}