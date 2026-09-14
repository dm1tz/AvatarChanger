using PluginLocale = AvatarChanger.Localization;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam.Interaction;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Web.Responses;
using SteamKit2;
using System.Collections.Generic;
using System.ComponentModel;
using System.Composition;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;
using static ArchiSteamFarm.Steam.Integration.ArchiWebHandler;

namespace AvatarChanger;

[Export(typeof(IPlugin))]
internal sealed class AvatarChangerPlugin : IBotCommand2, IGitHubPluginUpdates {
	internal const int MaxImageBytes = 5 * 1024 * 1024;

	private static readonly Uri UploadURL = new(SteamCommunityURL, "/actions/FileUploader");
	private static readonly Uri ProfileURL = new(SteamCommunityURL, "/my/edit/avatar");

	public string Name => nameof(AvatarChangerPlugin);
	public string RepositoryName => "dm1tz/AvatarChanger";
	public Version Version => typeof(AvatarChangerPlugin).Assembly.GetName().Version ?? throw new InvalidOperationException(nameof(Version));

	public Task OnLoaded() => Task.CompletedTask;

	public async Task<string?> OnBotCommand(Bot bot, EAccess access, string message, string[] args, ulong steamID = 0) {
		ArgumentNullException.ThrowIfNull(bot);

		if (!Enum.IsDefined(access)) {
			throw new InvalidEnumArgumentException(nameof(access), (int) access, typeof(EAccess));
		}

		ArgumentException.ThrowIfNullOrEmpty(message);

		if ((args == null) || (args.Length == 0)) {
			throw new ArgumentNullException(nameof(args));
		}

		if ((steamID != 0) && !new SteamID(steamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(steamID));
		}

		return args[0].ToUpperInvariant() switch {
			"CHANGEAVATAR" or "CA" when args.Length == 3 => await ResponseChangeAvatar(access, args[1], args[2], steamID).ConfigureAwait(false),
			"CHANGEAVATAR" or "CA" when args.Length == 2 => await ResponseChangeAvatar(bot, access, args[1]).ConfigureAwait(false),
			"ACVERSION" or "ACV" => ResponseVersion(access),
			_ => null
		};
	}

	private static async Task<string?> ResponseChangeAvatar(Bot bot, EAccess access, string avatarURL) {
		if (!bot.IsConnectedAndLoggedOn) {
			return bot.Commands.FormatBotResponse(Strings.BotNotConnected);
		}

		if (access < EAccess.Master) {
			return access > EAccess.None ? bot.Commands.FormatBotResponse(Strings.ErrorAccessDenied) : null;
		}

		if (!TryGetImageURL(avatarURL, out Uri? imageURL)) {
			return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsInvalid, nameof(avatarURL)));
		}

		try {
			using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
			// Never send Steam cookies to the avatar URL. Respect the bot's configured proxy.
			using HttpClientHandler handler = new() {
				UseCookies = false,
				CheckCertificateRevocationList = true,
				Proxy = bot.BotConfig.WebProxy ?? ASF.GlobalConfig?.WebProxy,
				AllowAutoRedirect = true,
				MaxAutomaticRedirections = 5
			};

			using HttpClient client = new(handler) { Timeout = TimeSpan.FromSeconds(60) };
			byte[] image = await Download(client, imageURL!, timeout.Token).ConfigureAwait(false);

			// The session-aware HEAD validates/refreshes ASF's Steam Community session before upload.
			if (!await bot.ArchiWebHandler.UrlHeadWithSession(ProfileURL, cancellationToken: timeout.Token).ConfigureAwait(false)) {
				return bot.Commands.FormatBotResponse(PluginLocale.Strings.SessionUnavailable);
			}

			string? sessionID = bot.ArchiWebHandler.WebBrowser.CookieContainer.GetCookies(SteamCommunityURL)["sessionid"]?.Value;

			if (string.IsNullOrEmpty(sessionID)) {
				return bot.Commands.FormatBotResponse(string.Format(CultureInfo.CurrentCulture, Strings.ErrorIsEmpty, nameof(sessionID)));
			}

			using MultipartFormDataContent form = CreateUpload(image, bot.SteamID, sessionID);
			ObjectResponse<JsonElement>? result = await WebLimitRequest(SteamCommunityURL, () => bot.ArchiWebHandler.WebBrowser.UrlPostToJsonObject<JsonElement, MultipartFormDataContent>(UploadURL, data: form, referer: ProfileURL, maxTries: 1, cancellationToken: timeout.Token), timeout.Token).ConfigureAwait(false);

			return bot.Commands.FormatBotResponse((result != null) && ((int) result.StatusCode is >= 200 and < 300) && IsSuccess(result.Content) ? Strings.Success : PluginLocale.Strings.AvatarNotConfirmed);
		} catch (OperationCanceledException) {
			return bot.Commands.FormatBotResponse(PluginLocale.Strings.AvatarTimedOut);
		} catch (Exception e) {
			// Avoid echoing URL query tokens, passwords or session details in command responses.
			return bot.Commands.FormatBotResponse(PluginLocale.Strings.FormatAvatarFailed(e.GetType().Name));
		}
	}

	private static async Task<string?> ResponseChangeAvatar(EAccess access, string botNames, string avatarURL, ulong steamID = 0) {
		ArgumentException.ThrowIfNullOrEmpty(botNames);

		if ((steamID != 0) && !new SteamID(steamID).IsIndividualAccount) {
			throw new ArgumentOutOfRangeException(nameof(steamID));
		}

		HashSet<Bot>? bots = Bot.GetBots(botNames);

		if ((bots == null) || (bots.Count == 0)) {
			return access >= EAccess.Owner ? Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotNotFound, botNames)) : null;
		}

		IList<string?> results = await Utilities.InParallel(bots.Select(bot => ResponseChangeAvatar(bot, Commands.GetProxyAccess(bot, access, steamID), avatarURL))).ConfigureAwait(false);

		List<string> responses = [.. results.Where(static result => !string.IsNullOrEmpty(result)).Select(static result => result!)];

		return responses.Count > 0 ? string.Join(Environment.NewLine, responses) : null;
	}

	private static string? ResponseVersion(EAccess access) {
		if (access < EAccess.FamilySharing) {
			return access > EAccess.None ? Commands.FormatStaticResponse(Strings.ErrorAccessDenied) : null;
		}

		return Commands.FormatStaticResponse(string.Format(CultureInfo.CurrentCulture, Strings.BotVersion, nameof(AvatarChangerPlugin), typeof(AvatarChangerPlugin).Assembly.GetName().Version));
	}

	private static bool TryGetImageURL(string avatarURL, out Uri? imageURL) => Uri.TryCreate(avatarURL, UriKind.Absolute, out imageURL) && (imageURL.Scheme is "http" or "https") && string.IsNullOrEmpty(imageURL.UserInfo);

	internal static async Task<byte[]> Download(HttpClient client, Uri imageURL, CancellationToken cancellationToken) {
		using HttpResponseMessage response = await client.GetAsync(imageURL, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
		response.EnsureSuccessStatusCode();

		if (response.Content.Headers.ContentLength > MaxImageBytes) {
			throw new InvalidDataException(PluginLocale.Strings.AvatarTooLarge);
		}

		Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
		await using ConfiguredAsyncDisposable inputLifetime = input.ConfigureAwait(false);
		using MemoryStream output = new();
		byte[] buffer = new byte[8192];
		int read;

		while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0) {
			if (output.Length + read > MaxImageBytes) {
				throw new InvalidDataException(PluginLocale.Strings.AvatarTooLarge);
			}

			await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
		}

		byte[] data = output.ToArray();
		GetImageType(data);
		return data;
	}

	internal static (string Extension, string MediaType) GetImageType(ReadOnlySpan<byte> data) {
		if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) {
			return ("png", "image/png");
		}

		if (data.StartsWith(new byte[] { 255, 216, 255 })) {
			return ("jpg", "image/jpeg");
		}

		if (data.StartsWith("GIF87a"u8) || data.StartsWith("GIF89a"u8)) {
			return ("gif", "image/gif");
		}

		throw new InvalidDataException(PluginLocale.Strings.AvatarInvalidImage);
	}

	[SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "MultipartFormDataContent owns its added content and is disposed by the caller.")]
	internal static MultipartFormDataContent CreateUpload(byte[] data, ulong steamID, string sessionID) {
		ArgumentException.ThrowIfNullOrEmpty(sessionID);
		(string extension, string mediaType) = GetImageType(data);
		MultipartFormDataContent form = new();
		form.Add(new StringContent(data.Length.ToString(CultureInfo.InvariantCulture)), "MAX_FILE_SIZE");
		form.Add(new StringContent("player_avatar_image"), "type");
		form.Add(new StringContent(steamID.ToString(CultureInfo.InvariantCulture)), "sId");
		form.Add(new StringContent(sessionID), "sessionid");
		form.Add(new StringContent("1"), "doSub");
		form.Add(new StringContent("1"), "json");
		ByteArrayContent image = new(data);
		image.Headers.ContentType = new MediaTypeHeaderValue(mediaType);
		form.Add(image, "avatar", $"avatar.{extension}");
		return form;
	}

	internal static bool IsSuccess(JsonElement response) => response.ValueKind == JsonValueKind.Object && response.TryGetProperty("success", out JsonElement success) && ((success.ValueKind == JsonValueKind.True) || ((success.ValueKind == JsonValueKind.Number) && success.TryGetInt32(out int value) && (value == 1)));
}
