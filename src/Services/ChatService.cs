using System.Globalization;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reflection;
using System.Text;
using App.Attributes;
using App.Extensions;
using Discord;
using Discord.Commands;
using Discord.Interactions;
using Discord.Rest;
using Discord.WebSocket;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Newtonsoft.Json.Linq;
using RestSharp;

namespace App.Services;

[Service]
public class ChatService : IDisposable {

	#region <<---------- Initializers ---------->>

	public ChatService(DiscordSocketClient discord, CommandService commands, LoggingService loggingService, IConfigurationRoot configurationRoot, GuildSettingsService guildSettings) {
		_disposable?.Dispose();
		_disposable = new CompositeDisposable();

		// dependecies injected
		_discord = discord;
		_commands = commands;
		_log = loggingService;
		_config = configurationRoot;
		_guildSettings = guildSettings;

		_discord.MessageReceived += MessageReceivedAsync;
		_discord.MessageDeleted += MessageDeletedAsync;
		_discord.MessageUpdated += OnMessageUpdated;

		// first triggers
		Observable.Timer(TimeSpan.FromSeconds(5)).Subscribe(async _ => {
			// status
			await _discord.SetGameAsync($"v{Program.VERSION}");

			await HourlyMessage();
		}).AddTo(_disposable);


		// status
		Observable.Timer(TimeSpan.FromMinutes(30)).Repeat().Subscribe(async _ => { await UpdateSelfStatusAsync(); }).AddTo(_disposable);

		// hora em hora
		Observable.Timer(TimeSpan.FromMinutes(1)).Repeat().Subscribe(async _ => { await HourlyMessage(); }).AddTo(_disposable);
	}


	#endregion <<---------- Initializers ---------->>




	#region <<---------- Properties ---------->>

	readonly DiscordSocketClient _discord;
	readonly CommandService _commands;
	readonly LoggingService _log;
	readonly IConfigurationRoot _config;
	readonly GuildSettingsService _guildSettings;
	readonly Random _rand = new();

	System.Timers.Timer _bumpTimer;
	int _previousHour = -1;

	CompositeDisposable _disposable;

	#endregion <<---------- Properties ---------->>




	#region <<---------- Callbacks ---------->>

	async Task MessageReceivedAsync(SocketMessage socketMessage) {
		if (!(socketMessage is SocketUserMessage userMessage)) return;

		// private message
		if (userMessage.Channel is IDMChannel dmChannel) {
			await PrivateMessageReceivedAsync(socketMessage, dmChannel);
			return;
		}

		if (userMessage.Source != MessageSource.User) return;
		await UserMessageReceivedAsync(userMessage);
	}

	async Task MessageDeletedAsync(Cacheable<IMessage, ulong> cacheable, Cacheable<IMessageChannel, ulong> cacheable1) {
		if (!cacheable.HasValue) return;
		var message = cacheable.Value;
		_log.Warning($"[MessageDeleted] from {message.Author.Username}: '{message.Content}'");
	}


	async Task OnMessageUpdated(Cacheable<IMessage, ulong> cacheable, SocketMessage msg, ISocketMessageChannel channel) {
		await MessageReceivedAsync(msg);
	}

	async Task PrivateMessageReceivedAsync(SocketMessage socketMessage, IDMChannel dmChannel) {
		_log.Info($"Private message received from {socketMessage.Author}: {socketMessage.Content}");

		if (socketMessage.Content.ToLower() == ",ip") {
			var ip = await GetBotPublicIp();
			await dmChannel.SendMessageAsync($"Meu IP:```{ip}```");
		}

		var dm = await _discord.GetDMChannelAsync(203373041063821313);
		if (dm == null) return;
		await dm.SendMessageAsync($"```{socketMessage.Author.Username} me mandou DM:```{socketMessage.Content}");
		if (socketMessage.Attachments != null) {
			foreach (var attachment in socketMessage.Attachments) {
				await dm.SendFileAsync(attachment.Url, attachment.Filename);
			}
		}
	}

	#endregion <<---------- Callbacks ---------->>




	#region <<---------- Message Answer ---------->>

	async Task UserMessageReceivedAsync(SocketUserMessage userMessage)
	{
		if(userMessage.Author.IsBot) return;
		if (string.IsNullOrEmpty(userMessage.Content)) return;

		// Parameters
		bool userSaidHerName = false;
		bool isQuestion = false;

		#region <<---------- Setup message string to read ---------->>

		// Content of the message in lower case string.
		string messageString = userMessage.Content.ToLower();

		messageString = RemoveDiacritics(messageString);

		messageString = messageString.Trim();

		// if the message is a question
		if (messageString.Contains('?')) {
			messageString = messageString.Replace("?", string.Empty);
			isQuestion = true;
		}

		// remove double and tripple spaces
		messageString = messageString.Replace("  ", " ").Replace("   ", " ");

		// See if message is empty now
		if (messageString.Length <= 0) {
			return;
		}

		#endregion <<---------- Setup message string to read ---------->>

		// #region <<---------- User Specific ---------->>
		//
		// // babies
		// try {
		// 	var jsonArray = (await JsonCache.LoadValueAsync("UsersBabies", "data")).AsArray;
		// 	for (int i = 0; i < jsonArray.Count; i++) {
		// 		var userId = jsonArray[i].Value;
		// 		if (string.IsNullOrEmpty(userId)) continue;
		// 		if (userMessage.Author.Id != Convert.ToUInt64(userId)) continue;
		// 		await userMessage.AddReactionAsync(new Emoji("😭"));
		// 		break;
		// 	}
		// } catch (Exception e) {
		// 	await this._log.Error($"Exception trying to process babies answer: {e.ToString()}");
		// }
		//
		// #endregion <<---------- User Specific ---------->>

		#region Fast Answers

		if (messageString == ("ping")) {
			await userMessage.Channel.SendMessageAsync("pong");
			return;
		}
		if (messageString == ("pong")) {
			await userMessage.Channel.SendMessageAsync("ping");
			return;
		}

		if (messageString == ("marco")) {
			await userMessage.Channel.SendMessageAsync("polo");
			return;
		}
		if (messageString == ("polo")) {
			await userMessage.Channel.SendMessageAsync("marco");
			return;
		}

		if (messageString == ("dotto")) {
			await userMessage.Channel.SendMessageAsync("Dotto. :musical_note:");
			return;
		}

		if (messageString == "❤" || messageString == ":heart:") {
			await userMessage.Channel.SendMessageAsync("❤");
			return;
		}

		if (messageString == ":broken_heart:" || messageString == "💔") {
			await userMessage.Channel.SendMessageAsync("❤");
			await userMessage.AddReactionAsync(new Emoji("😥"));
			return;
		}

		if (messageString == ("ne") || messageString == ("neh")) {
			await userMessage.Channel.SendMessageAsync(ChooseAnAnswer(new[] {"Isso ai.", "Pode crê.", "Boto fé."}));
			return;
		}

		if (messageString == ("vlw") || messageString == ("valeu") || messageString == ("valew")) {
			await userMessage.AddReactionAsync(new Emoji("😉"));
			return;
		}

		// see if message is an Hi
		if (messageString == "oi"
		    || messageString == "ola"
		    || messageString == "hi"
		    || messageString == "hello"
		    || messageString == "coe"
		    || messageString == "ola pessoas"
		    || messageString == "coe rapaziada"
		    || messageString == "dae"
		    || messageString == "oi galera"
		    || messageString == "dae galera"
		    || messageString == "opa"
		   ) {
			await userMessage.Channel.SendMessageAsync(ChooseAnAnswer(new[] {
				"oi", "olá", "hello", "coé", "oin", "aoba", "fala tu", "manda a braba", "opa"
			}));
			return;
		}

		// see if message has an BYE
		if (messageString == "tchau"
		    || messageString == "xau"
		    || messageString == "tiau"
		    || messageString == "thau"
		    || messageString == "xau"
		    || messageString == "flw"
		    || messageString == "flws"
		    || messageString == "falou"
		    || messageString == "falous"
		    || messageString.Contains(" flw")
		   ) {
			await userMessage.Channel.SendMessageAsync(ChooseAnAnswer(new[] {
				"tchau", "xiau", "bye bye", "flw"
			}));
			return;
		}

		#endregion

		#region Nyu
		// check if user said nyu / nuy
		if (userSaidHerName) {
			if (HasAtLeastOneWord(messageString, new[] {"serve", "faz"})) {
				if (isQuestion) {
					await userMessage.Channel.SendMessageAsync("Sou um bot que responde diversas perguntas sobre assuntos comuns aqui no servidor. Com o tempo o Chris me atualiza com mais respostas e reações.");
					return;
				}
			}

			// Praises
			if (messageString.Contains("gata")
			    || messageString.Contains("cremosa")
			    || messageString.Contains("gostosa")
			    || messageString.Contains("gatinha")
			    || messageString.Contains("linda")
			    || messageString.Contains("delicia")
			    || messageString.Contains("dlicia")
			    || messageString.Contains("dlcia")
			    || messageString == ("amo te")
			    || messageString == ("ti amu")
			    || messageString == ("ti amo")
			    || messageString == ("ti adoro")
			    || messageString == ("te adoro")
			    || messageString == ("te amo")
			    || messageString == ("obrigado")
			    || messageString == ("obrigada")
			   ) {
				await userMessage.AddReactionAsync(new Emoji("❤"));
				return;
			}

		}
		#endregion

		#region General

		if (messageString == "alguem ai") {
			await userMessage.Channel.SendMessageAsync("Eu");
			return;
		}

		#endregion

		#region Insults
		// Answer to insults

		if (messageString.Contains("bot lixo")
		    || messageString.Contains("suamaeeminha")
		   ) {
			await userMessage.AddReactionAsync(new Emoji("👀"));
			return;
		}
		#endregion

		//!!! THIS PART OF THE CODE BELOW MUST BE AS THE LAST BECAUSE:
		// TODO bot log unknown commands on file
		// see if user sayd only bot name on message with some other things and she has no answer yet
		// if (userSaidHerName) {
		// 	string unknownCommandsFileName = "Lists/unknownCommands.txt";
		// 	string textToWrite = messageString + $"	({userMessage.Author.Username})";
		//
		// 	// first, compare if the text to save its not to big
		// 	if (textToWrite.Length > 48) {
		// 		// ignore the message because it can be spam
		// 		return;
		// 	}
		//
		// 	// check if the txt its not biggen then 10mb
		// 	await using (var ss = new StreamWriter(unknownCommandsFileName)) {
		//
		// 	}
		// 	var fileInfo = new FileInfo(unknownCommandsFileName);
		// 	if (fileInfo.Length > 10 * 1000000) {
		// 		await userMessage.Channel.SendMessageAsync("<@203373041063821313> eu tentei adicionar o texto que o " + userMessage.Author.Username + " digitou mas o arquivo de lista de comandos alcançou o tamanho limite. :sob:");
		// 		return;
		// 	}
		//
		// 	// get text in string
		// 	string fileContent = File.ReadAllText(unknownCommandsFileName);
		// 	if (fileContent != null) {
		// 		// only write if the unknown text is NOT already on the file
		// 		if (!fileContent.Contains(messageString)) {
		// 			File.AppendAllText(unknownCommandsFileName, textToWrite + Environment.NewLine);
		// 			await userMessage.AddReactionAsync(new Emoji("❔"));
		// 			return;
		// 		}
		// 	}
		// 	else {
		// 		File.AppendAllText(unknownCommandsFileName, textToWrite + Environment.NewLine);
		// 		await userMessage.AddReactionAsync(new Emoji("❔"));
		// 		return;
		// 	}
		//
		// 	// return "Ainda não tenho resposta para isso:\n" + "`" + messageString + "`";
		// 	return;
		// }

		// if arrived here, the message has no answer.
	}

	#endregion <<---------- Message Answer ---------->>




	#region <<---------- User ---------->>

	public async Task UpdateSelfStatusAsync() {
		var statusText = $"v{Program.VERSION.ToString()}";
		try {
			var activitiesJsonArray = JsonCache.LoadFromJson<JArray>("BotStatus");
			if(!activitiesJsonArray.HasValues) {
				throw new Exception("No bot status options found");
			}
			var index = _rand.Next(0, activitiesJsonArray.Count);
			var answers = activitiesJsonArray.ElementAt(index)["answers"];
			var statusTextArray = answers;
			var selectedStatus = statusTextArray[_rand.Next(0, statusTextArray.Count())];
			await _discord.SetGameAsync(
				selectedStatus.Value<string>(),
				(ActivityType)index == ActivityType.Streaming ? "https://twitch.tv/chrisdbhr" : null,
				(ActivityType)index
			);

		} catch (Exception e) {
			_log.Error(e.Message);
			if (_discord == null) return;
			await _discord.SetGameAsync(statusText, null, ActivityType.Watching);
		}
	}

	#endregion <<---------- User ---------->>




	#region <<---------- Bot IP ---------->>

	async Task<string> GetBotPublicIp() {
		var client = new RestClient();
		var timeline = await client.ExecuteAsync(new RestRequest("http://ipinfo.io/ip", Method.Get));

		if (!string.IsNullOrEmpty(timeline.ErrorMessage)) {
			_log.Error($"Error trying to get bot IP: {timeline.ErrorMessage}");
			return null;
		}
		if (string.IsNullOrEmpty(timeline.Content)) return null;
		return timeline.Content.Trim();
	}

	#endregion <<---------- Bot IP ---------->>


	async Task HourlyMessage() {
		var time = DateTime.UtcNow.AddHours(-3);
		if (time.Minute != 0) return;

		foreach (var guild in _discord.Guilds) {
			try {
				string title = time.ToString("h tt", CultureInfo.InvariantCulture);
				string msg = null;
				var guildSettings = _guildSettings.GetGuildSettings(guild.Id);
				if (guildSettings.HourlyMessageChannelId == null) continue;

				var channel = guild.GetTextChannel(guildSettings.HourlyMessageChannelId.Value);
				if (channel == null) continue;

				switch (time.Hour) {
					case 0:
						title = "Meia noite, vão dormi";
						msg = $"Horário oficial do óleo de macaco";
						break;
					case 12:
						title = "Meio dia";
						msg = $"Hora de comer *nhon nhon nhon*";
						break;
				}

				if (channel.CachedMessages.Count <= 0) return;

				var lastUserMsg = channel.CachedMessages.OrderBy(m => m.Timestamp).Last() as IUserMessage;

				bool lastMsgIsFromThisBot = lastUserMsg != null && lastUserMsg.Author.Id == _discord.CurrentUser.Id;

				// motivation phrase
				if (string.IsNullOrEmpty(msg)) {
					msg = await GetRandomMotivationPhrase();
				}
				msg = string.IsNullOrEmpty(msg) ? "Hora agora" : $"*\"{msg}\"*";

				var embed = new EmbedBuilder {
					Title = title,
					Description = msg
				};


				RestUserMessage msgSend = null;
				if (lastMsgIsFromThisBot) {
					if (lastUserMsg.MentionedUserIds.Count <= 0) {
						await lastUserMsg.ModifyAsync(p =>
							p.Embed = embed.Build()
						);
					}
				}
				else {
					msgSend = await channel.SendMessageAsync(string.Empty, false, embed.Build());
				}

				// get random photo
				try {
					var client = new RestClient();
					var timeline = await client.ExecuteAsync(new RestRequest("https://picsum.photos/96", Method.Get));
					if (!string.IsNullOrEmpty(timeline.ResponseUri.OriginalString)) {
						embed.ThumbnailUrl = timeline.ResponseUri.OriginalString;
						if(msgSend != null) await msgSend.ModifyAsync(p => p.Embed = embed.Build());
					}
				} catch (Exception e) {
					_log.Error(e.ToString());
				}

			} catch (Exception e) {
				_log.Error(e.ToString());
				continue;
			}

		}
	}



	#region <<---------- Chat Messages ---------->>

	#endregion <<---------- Chat Messages ---------->>




	#region <<---------- Pensador API ---------->>

	public async Task<string> GetRandomMotivationPhrase() {
		var phrases = await GetListOfMotivationalPhrases();
		if (!phrases.Any()) return null;
		return phrases
			.Where(p => !p.ToLower().Contains("deus") || !p.ToLower().Contains("senhor"))
			.OrderBy(p => p.Length)
			.Take(phrases.Count / 2)
			.RandomElement();
	}

	async Task<List<string>> GetListOfMotivationalPhrases() {
		var client = new RestClient();
		var timeline = await client.ExecuteAsync(new RestRequest("https://www.pensador.com/recentes", Method.Get));

		if (!string.IsNullOrEmpty(timeline.ErrorMessage) || string.IsNullOrEmpty(timeline.Content)) {
			_log.Error($"Error trying Random Motivation Phrase: {timeline.ErrorMessage}");
			return null;
		}

		var html = new HtmlDocument();
		html.LoadHtml(timeline.Content);
		var nodeCollection = html.DocumentNode.SelectNodes("//p");

		var listOfPhrases = new List<string>();
		foreach (var node in nodeCollection) {
			if (string.IsNullOrEmpty(node.Id)) continue;
			listOfPhrases.Add(node.InnerText);
		}

		return listOfPhrases;
	}

	#endregion <<---------- Pensador API ---------->>




	#region <<---------- String Threatment ---------->>

	/// <summary>
	/// Check if a string contains all defined words.
	/// </summary>
	/// <param name="message">Full string to compare.</param>
	/// <param name="s">Words to check.</param>
	/// <returns>Return if there is all of words in message.</returns>
	public static bool HasAllWords(string message, string[] s) {
		for (int i = 0; i < s.Length; i++) {
			if (!message.Contains(s[i])) {
				return false;
			}
		}
		return true;
	}

	/// <summary>
	/// Check if a string contains at least one of defined words.
	/// </summary>
	/// <param name="message">Full string to compare.</param>
	/// <param name="s">Words to check.</param>
	/// <returns>Return true if there is a word in message.</returns>
	public static bool HasAtLeastOneWord(string message, string[] s) {
		return s.Any(c => message.Contains(c));
	}

	/// <summary>
	/// Chosse a string between an array of strings.
	/// </summary>
	/// <param name="s">strings to choose, pass as new[] { "option1", "option2", "..." }</param>
	/// <returns>return the choose string</returns>
	public static string ChooseAnAnswer(string[] s) {
		if (s.Length > 1) {
			return s[new Random().Next(0, s.Length)];
		}

		// equals one
		return s[0];
	}

	/// <summary>
	/// Remove special characters from string.
	/// </summary>
	/// <param name="text"></param>
	/// <returns>Return normalized string.</returns>
	public static string RemoveDiacritics(string text) {
		var normalizedString = text.Normalize(NormalizationForm.FormD);
		var stringBuilder = new StringBuilder();

		foreach (var c in normalizedString) {
			var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
			if (unicodeCategory != UnicodeCategory.NonSpacingMark) {
				stringBuilder.Append(c);
			}
		}

		return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
	}

	/// <summary>
	/// Removes bot string from message, and trim string, also set a boolean.
	/// </summary>
	public static string RemoveBotNameFromMessage(string messageString) {
		messageString = messageString.Replace("nyu", "");
		messageString = messageString.Replace("nuy", "");
		messageString = messageString.Trim();
		return messageString;
	}

	#endregion <<---------- String Threatment ---------->>




	#region <<---------- Disposable ---------->>

	public void Dispose() {
		_discord?.Dispose();
		((IDisposable) _commands)?.Dispose();
		_bumpTimer?.Dispose();
		_disposable?.Dispose();
	}

	#endregion <<---------- Disposable ---------->>

}