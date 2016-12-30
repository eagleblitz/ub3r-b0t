﻿namespace UB3RB0T
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Threading;
    using System.Threading.Tasks;
    using Discord;
    using Discord.WebSocket;
    using UB3RIRC;
    using System.Collections.Concurrent;
    using System.Text.RegularExpressions;

    public partial class Bot
    {
        private int shard = 0;
        private BotType botType;
        private DiscordSocketClient client;

        // TODO: Wire up audio support once Discord.NET supports it.
        // private IAudioClient _audio;

        private Dictionary<string, IrcClient> ircClients;

        private ConcurrentDictionary<string, int> commandsIssued = new ConcurrentDictionary<string, int>();

        private static Regex channelRegex = new Regex("#([a-zA-Z0-9\\-]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static Regex httpRegex = new Regex("https?://([^\\s]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static Regex TimerRegex = new Regex(".*?remind (?<target>.+?) in (?<years>[0-9]+ year)?s? ?(?<weeks>[0-9]+ week)?s? ?(?<days>[0-9]+ day)?s? ?(?<hours>[0-9]+ hour)?s? ?(?<minutes>[0-9]+ minute)?s? ?(?<seconds>[0-9]+ seconds)?.*?(?<prep>[^ ]+) (?<reason>.+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        public static Regex Timer2Regex = new Regex(".*?remind (?<target>.+?) (?<prep>[^ ]+) (?<reason>.+?) in (?<years>[0-9]+ year)?s? ?(?<weeks>[0-9]+ week)?s? ?(?<days>[0-9]+ day)?s? ?(?<hours>[0-9]+ hour)?s? ?(?<minutes>[0-9]+ minute)?s? ?(?<seconds>[0-9]+ seconds)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        private Timer notificationsTimer;
        private Timer settingsUpdateTimer;
        private Timer remindersTimer;
        private Timer packagesTimer;
        private Timer statsTimer;
        private Timer oneMinuteTimer;

        private Logger consoleLogger = Logger.GetConsoleLogger();

        // TODO: Genalize this API support -- currently specific to private API
        private BotApi BotApi;

        public Bot(BotType botType, int shard)
        {
            this.botType = botType;
            this.shard = shard;
        }

        public BotConfig Config => BotConfig.Instance;

        /// Initialize and connect to the desired clients, hook up event handlers.
        /// </summary>
        /// <summary>
        public async Task RunAsync()
        {
            if (botType == BotType.Discord)
            {
                if (string.IsNullOrEmpty(this.Config.Discord.Token))
                {
                    throw new InvalidConfigException("Discord auth token is missing.");
                }

                await this.CreateDiscordBotAsync();
            }
            else
            {
                if (this.Config.Irc.Servers == null)
                {
                    throw new InvalidConfigException("Irc server list is missing.");
                }

                if (this.Config.Irc.Servers.Any(s => s.Channels.Any(c => !c.StartsWith("#"))))
                {
                    throw new InvalidConfigException("Invalid channel specified; all channels should start with #.");
                }

                await this.CreateIrcBotsAsync();
            }

            // If a custom API endpoint is supported...support it
            if (this.Config.ApiEndpoint != null)
            {
                this.BotApi = new BotApi(this.Config.ApiEndpoint, this.Config.ApiKey, this.botType);
            }

            // TODO: I see a pattern here.  Clean this up.
            notificationsTimer = new Timer(CheckNotificationsAsync, null, 10000, 10000);
            remindersTimer = new Timer(CheckRemindersAsync, null, 10000, 10000);
            oneMinuteTimer = new Timer(OneMinuteTimer, null, 60000, 60000);
            packagesTimer = new Timer(CheckPackagesAsync, null, 1800000, 1800000);

            string read = string.Empty;
            while (read != "exit")
            {
                read = Console.ReadLine();

                string[] argv = read.Split(new char[] { ' ' }, 4);

                switch (argv[0])
                {
                    case "reload":
                        JsonConfig.ConfigInstances.Clear();
                        Console.WriteLine("Config reloaded.");
                        break;

                    default:
                        break;
                }
            }

            await this.client.DisconnectAsync();
            Console.WriteLine("Exited.");
        }

        private static void Heartbeat()
        {

        }

        private async Task UpdateSettingsAsync()
        {
            try
            {
                Console.WriteLine("Fetching server settings...");
                await SettingsConfig.Instance.OverrideAsync(this.Config.SettingsEndpoint);
                Console.WriteLine("Server settings updated.");
            }
            catch (Exception ex)
            {
                // TODO: Update to using one of the logging classes (Discord/IRC)
                Console.WriteLine($"Failed to update server settings: {ex}");
            }
        }

        private bool processingtimers = false;
        private async void CheckRemindersAsync(object state)
        {
            if (processingtimers) { return; }
            processingtimers = true;
            if (CommandsConfig.Instance.RemindersEndpoint != null)
            {
                var reminders = await Utilities.GetApiResponseAsync<ReminderData[]>(CommandsConfig.Instance.RemindersEndpoint);
                if (reminders != null)
                {
                    var remindersToDelete = new List<string>();
                    foreach (var timer in reminders.Where(t => t.BotType == this.botType))
                    {
                        string requestedBy = string.IsNullOrEmpty(timer.Requestor) ? string.Empty : "[Requested by " + timer.Requestor + "]";


                        if (this.botType == BotType.Irc)
                        {
                            string msg = string.Format("{0}: {1} ({2} ago) {3}", timer.Nick, timer.Reason, timer.Duration, requestedBy);
                            this.ircClients.Values.FirstOrDefault(c => c.Host == timer.Server)?.Command("PRIVMSG", timer.Channel, msg);
                            remindersToDelete.Add(timer.Id);
                        }
                        else
                        {
                            if (this.client.GetChannel(Convert.ToUInt64(timer.Channel)) is ISocketMessageChannel channel)
                            {
                                try
                                {
                                    string nick = timer.Nick;
                                    if (channel is IGuildChannel guildChan)
                                    {
                                        nick = (await guildChan.Guild.GetUsersAsync().ConfigureAwait(false)).Find(nick).FirstOrDefault()?.Mention ?? nick;
                                    }

                                    string msg = string.Format("{0}: {1} ({2} ago) {3}", nick, timer.Reason, timer.Duration, requestedBy);
                                    await channel.SendMessageAsync(msg);
                                    remindersToDelete.Add(timer.Id);
                                }
                                catch (Exception ex)
                                {
                                    // TODO: logging
                                    Console.WriteLine(ex);
                                }
                            }
                        }
                    }

                    await Utilities.GetApiResponseAsync<object>(new Uri(CommandsConfig.Instance.RemindersEndpoint.ToString() + "?ids=" + string.Join(",", remindersToDelete)));
                }
            }

            processingtimers = false;
        }

        private void OneMinuteTimer(object state)
        {
            this.commandsIssued.Clear();
        }

        private bool processingnotifications = false;
        private async void CheckNotificationsAsync(object state)
        {
            if (processingnotifications) { return; }
            processingnotifications = true;
            if (CommandsConfig.Instance.NotificationsEndpoint != null)
            {
                var notifications = await Utilities.GetApiResponseAsync<NotificationData[]>(CommandsConfig.Instance.NotificationsEndpoint);
                if (notifications != null)
                {
                    var notificationsToDelete = new List<string>();
                    foreach (var notification in notifications.Where(t => t.BotType == this.botType))
                    {
                        if (this.botType == BotType.Irc)
                        {
                            // Pending support
                        }
                        else
                        {
                            if (this.client.GetChannel(Convert.ToUInt64(notification.Channel)) is ITextChannel channel)
                            {
                                notificationsToDelete.Add(notification.Id);

                                if ((channel.Guild as SocketGuild).CurrentUser.GetPermissions(channel).SendMessages)
                                {
                                    await channel.SendMessageAsync(notification.Text);
                                }
                            }
                            else if (this.client.GetGuild(Convert.ToUInt64(notification.Server)) is IGuild guild)
                            {
                                notificationsToDelete.Add(notification.Id);

                                var defaultChannel = await guild.GetDefaultChannelAsync();
                                var botGuildUser = await defaultChannel.GetUserAsync(this.client.CurrentUser.Id);
                                if ((defaultChannel.Guild as SocketGuild).CurrentUser.GetPermissions(defaultChannel).SendMessages)
                                {
                                    defaultChannel?.SendMessageAsync($"(Configured notification channel no longer exists, please fix it in the settings!) {notification.Text}");
                                }
                            }
                        }
                    }

                    await Utilities.GetApiResponseAsync<object>(new Uri(CommandsConfig.Instance.NotificationsEndpoint.ToString() + "?ids=" + string.Join(",", notificationsToDelete)));
                }
            }

            processingnotifications = false;
        }

        private async void CheckPackagesAsync(object state)
        {
            if (CommandsConfig.Instance.PackagesEndpoint != null)
            {
                var packages = await Utilities.GetApiResponseAsync<PackageData[]>(CommandsConfig.Instance.PackagesEndpoint);
                if (packages != null)
                {
                    foreach (var package in packages.Where(t => t.BotType == this.botType))
                    {
                        string query = $"ups bot {package.Tracking}";
                        var messageData = new BotMessageData(this.botType)
                        {
                            UserName = package.Nick,
                            Channel = package.Channel,
                            Server = package.Server,
                            UserId = string.Empty,
                        };

                        var responses = await this.BotApi.IssueRequestAsync(messageData, query);

                        if (this.botType == BotType.Irc)
                        {
                            foreach (var response in responses)
                            {
                                this.ircClients.Values.FirstOrDefault(c => c.Host == package.Server)?.Command("PRIVMSG", package.Channel, response);
                            }
                        }
                        else
                        {
                            if (this.client.GetChannel(Convert.ToUInt64(package.Channel)) is ITextChannel channel)
                            {
                                if ((channel.Guild as SocketGuild).CurrentUser.GetPermissions(channel).SendMessages)
                                {
                                    if (responses.Length > 0 && !string.IsNullOrEmpty(responses[0]))
                                    {
                                        string senderNick = package.Nick;
                                        var user = (await channel.Guild.GetUsersAsync().ConfigureAwait(false)).Find(package.Nick).FirstOrDefault();
                                        if (user != null)
                                        {
                                            senderNick = user.Mention;
                                        }

                                        await channel.SendMessageAsync($"{senderNick} oshi- an upsdate!");

                                        foreach (var response in responses)
                                        {
                                            if (!string.IsNullOrEmpty(response))
                                            {

                                                await channel.SendMessageAsync(response);
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        private async Task<List<string>> ProcessMessageAsync(BotMessageData messageData) => await ProcessMessageAsync(messageData, new Settings());

        private async Task<List<string>> ProcessMessageAsync(BotMessageData messageData, Settings settings)
        {
            var responses = new List<string>();

            if (this.BotApi != null)
            {
                // if an explicit command is being used, it wins out over any implicitly parsed command
                string query = messageData.Query;
                string command = messageData.Command;
                string[] contentParts = messageData.Content.Split(new[] { ' ' });

                if (string.IsNullOrEmpty(command))
                {
                    // check for reminders
                    Match timerMatch = TimerRegex.Match(messageData.Content);
                    Match timer2Match = Timer2Regex.Match(messageData.Content);

                    if (timerMatch.Success || timer2Match.Success)
                    {
                        Match matchToUse = timerMatch.Success && !timerMatch.Groups["prep"].Value.All(char.IsDigit) ? timerMatch : timer2Match;
                        if (Utilities.TryParseReminder(matchToUse, messageData, out query))
                        {
                            command = "timer";
                        }
                    }
                    else if (settings.AutoTitlesEnabled && CommandsConfig.Instance.AutoTitleMatches.Any(t => messageData.Content.Contains(t)))
                    {
                        Match match = httpRegex.Match(messageData.Content);
                        if (match != null)
                        {
                            command = "title";
                            query = $"{command} {match.Value}";
                        }
                    }
                    else if (settings.FunResponsesEnabled && contentParts.Length > 1 && contentParts[1] == "face")
                    {
                        command = "face";
                        query = $"{command} {contentParts[0]}";
                    }
                }

                // Ignore if the command is disabled on this server
                if (settings.IsCommandDisabled(CommandsConfig.Instance, command))
                {
                    return responses;
                }

                if (!string.IsNullOrEmpty(command) && CommandsConfig.Instance.Commands.ContainsKey(command))
                {
                    // make sure we're not rate limited
                    var commandKey = command + messageData.Server;
                    var commandCount = this.commandsIssued.AddOrUpdate(commandKey, 1, (key, val) =>
                    {
                        return val + 1;
                    });

                    if (commandCount > 10)
                    {
                        responses.Add("rate limited try later");
                    }
                    else
                    {
                        responses.AddRange(await this.BotApi.IssueRequestAsync(messageData, query));
                    }
                }
            }

            if (responses.Count == 0)
            {
                bool mentionsBot = messageData.BotType == BotType.Discord ? messageData.DiscordMessageData.MentionedUsers.Count == 1 && messageData.DiscordMessageData.MentionedUsers.First().Id == client.CurrentUser.Id :
                    (messageData.IrcMessageData.Text.Contains(this.Config.Name));

                string response = null;
                if (mentionsBot)
                {
                    var responseValue = PhrasesConfig.Instance.PartialMentionPhrases.Where(kvp => messageData.Content.Contains(kvp.Key)).FirstOrDefault().Value;
                    if (!string.IsNullOrEmpty(responseValue))
                    { 
                        response = PhrasesConfig.Instance.Responses[responseValue].Random();
                    }
                }
                
                if (response == null && settings.FunResponsesEnabled && PhrasesConfig.Instance.ExactPhrases.ContainsKey(messageData.Content))
                {
                    response = PhrasesConfig.Instance.Responses[PhrasesConfig.Instance.ExactPhrases[messageData.Content]].Random();
                }

                if (response != null)
                {
                    response = response.Replace("%from%", messageData.UserName);
                    string[] resps = response.Split(new char[] { '|' });
                    responses.AddRange(resps);
                }
            }

            return responses;
        }
    }
}