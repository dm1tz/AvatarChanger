using PluginLocale = AvatarChanger.Localization;
using AvatarChanger;
using ArchiSteamFarm.Localization;
using ArchiSteamFarm.Steam.Interaction;
using ArchiSteamFarm.Steam;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net;
using SteamKit2;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Threading;
using System;

internal static class Program {
	private static int Passed;

	private static async Task Main() {
		TestLocalization();

		byte[] png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+aL1sAAAAASUVORK5CYII=");
		Check(AvatarChangerPlugin.GetImageType(png).MediaType == "image/png", "PNG signature");
		Check(AvatarChangerPlugin.GetImageType(new byte[] { 255, 216, 255, 224 }).Extension == "jpg", "JPEG signature");
		Check(AvatarChangerPlugin.GetImageType("GIF89a"u8).Extension == "gif", "GIF signature");
		await Throws<InvalidDataException>(() => { AvatarChangerPlugin.GetImageType("<html>"u8); return Task.CompletedTask; }, "HTML rejected");
		await Throws<InvalidDataException>(() => { AvatarChangerPlugin.GetImageType([]); return Task.CompletedTask; }, "empty image rejected");

		using MultipartFormDataContent form = AvatarChangerPlugin.CreateUpload(png, 76561198000000000, "test-session");
		Check(form.Count() == 7, "multipart field count");
		HttpContent Field(string name) => form.Single(p => p.Headers.ContentDisposition?.Name?.Trim('"') == name);
		Check(await Field("type").ReadAsStringAsync() == "player_avatar_image", "upload type");
		Check(await Field("sId").ReadAsStringAsync() == "76561198000000000", "account ID");
		Check(await Field("sessionid").ReadAsStringAsync() == "test-session", "session ID");
		Check(await Field("MAX_FILE_SIZE").ReadAsStringAsync() == png.Length.ToString(CultureInfo.InvariantCulture), "file length");
		Check((await Field("avatar").ReadAsByteArrayAsync()).SequenceEqual(png), "image bytes unchanged");
		Check(Field("avatar").Headers.ContentType?.MediaType == "image/png", "multipart MIME");
		Check(Field("avatar").Headers.ContentDisposition?.FileName?.Trim('"') == "avatar.png", "multipart filename");

		foreach (string json in new[] { "{\"success\":true}", "{\"success\":1}" }) {
			using JsonDocument d = JsonDocument.Parse(json);
			Check(AvatarChangerPlugin.IsSuccess(d.RootElement), "Steam success response");
		}
		foreach (string json in new[] { "{}", "null", "{\"success\":false}", "{\"success\":0}", "{\"success\":\"1\"}", "{\"success\":2}" }) {
			using JsonDocument d = JsonDocument.Parse(json);
			Check(!AvatarChangerPlugin.IsSuccess(d.RootElement), "failure/malformed response rejected");
		}

		using (HttpClient client = new(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }))) {
			Check((await AvatarChangerPlugin.Download(client, new Uri("https://example.test/avatar"), CancellationToken.None)).SequenceEqual(png), "HTTP image download");
		}
		using (HttpClient client = new(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.NotFound)))) {
			await Throws<HttpRequestException>(() => AvatarChangerPlugin.Download(client, new Uri("https://example.test/avatar"), CancellationToken.None), "HTTP failure");
		}
		using (HttpClient client = new(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[AvatarChangerPlugin.MaxImageBytes + 1]) }))) {
			await Throws<InvalidDataException>(() => AvatarChangerPlugin.Download(client, new Uri("https://example.test/avatar"), CancellationToken.None), "oversize Content-Length");
		}
		using (HttpClient client = new(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new NonSeekableStream(new byte[AvatarChangerPlugin.MaxImageBytes + 1])) }))) {
			await Throws<InvalidDataException>(() => AvatarChangerPlugin.Download(client, new Uri("https://example.test/avatar"), CancellationToken.None), "oversize chunked body");
		}
		using (HttpClient client = new(new FakeHandler(() => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) }))) {
			await Throws<OperationCanceledException>(() => AvatarChangerPlugin.Download(client, new Uri("https://example.test/avatar"), new CancellationToken(true)), "cancelled download");
		}

		AvatarChangerPlugin plugin = new();
		// A disconnected SteamClient exercises connection-first responses without network requests.
		Bot bot = (Bot) RuntimeHelpers.GetUninitializedObject(typeof(Bot));
		typeof(Bot).GetField("SteamClient", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, new SteamClient());
		Commands commands = (Commands) Activator.CreateInstance(typeof(Commands), BindingFlags.Instance | BindingFlags.NonPublic, null, [bot], CultureInfo.InvariantCulture)!;
		typeof(Bot).GetField("<Commands>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(bot, commands);
		Check((await plugin.OnBotCommand(bot, EAccess.Master, "acv", ["acv"]))?.Contains(plugin.Version.ToString(), StringComparison.Ordinal) == true, "actual version dispatch");
		Check(await plugin.OnBotCommand(bot, EAccess.None, "acv", ["acv"]) == null, "version access gate");
		Check(await plugin.OnBotCommand(bot, EAccess.None, "ca https://example.test/a", ["ca", "https://example.test/a"]) == commands.FormatBotResponse(Strings.BotNotConnected), "connection check before access");
		Check(await plugin.OnBotCommand(bot, EAccess.Master, "other", ["other"]) == null, "actual unknown dispatch");
		foreach (string command in new[] { "changeavatar", "CA", "Ca" }) {
			Check(await plugin.OnBotCommand(bot, EAccess.Master, command, [command, "https://example.test/a.png?x=1&y=2"]) == commands.FormatBotResponse(Strings.BotNotConnected), "case-insensitive current bot dispatch");
		}

		foreach (string[] input in new[] { new[] { "ca" }, new[] { "changeavatar" }, new[] { "ca", "bot", "https://example.test/a", "extra" } }) {
			Check(await plugin.OnBotCommand(bot, EAccess.Master, string.Join(' ', input), input) == null, "unsupported arity ignored");
			Check(await plugin.OnBotCommand(bot, EAccess.None, string.Join(' ', input), input) == null, "unsupported arity hidden without access");
		}

		foreach (string command in new[] { "acv", "AcVersion" }) {
			Check((await plugin.OnBotCommand(bot, EAccess.Master, command, [command, "extra"]))?.Contains(plugin.Version.ToString(), StringComparison.Ordinal) == true, "version ignores extra arguments");
		}

		MethodInfo validateURL = typeof(AvatarChangerPlugin).GetMethod("TryGetImageURL", BindingFlags.Static | BindingFlags.NonPublic)!;
		foreach (string url in new[] { "file:///etc/passwd", "https://user:pass@example.test/a", "invalid" }) {
			Check(validateURL.Invoke(null, [url, null]) is false, "invalid avatar URL rejected");
		}
		foreach (string url in new[] { "https://example.test/a.png?x=1&y=2", "http://example.test/a.jpg" }) {
			Check(validateURL.Invoke(null, [url, null]) is true, "HTTP/HTTPS avatar URL accepted");
		}

		Check((await plugin.OnBotCommand(bot, EAccess.FamilySharing, "AcV", ["AcV"]))?.Contains(plugin.Version.ToString(), StringComparison.Ordinal) == true, "case-insensitive version at minimum access");
		Check(await plugin.OnBotCommand(bot, EAccess.Master, "other args", ["other", "args"]) == null, "unknown command with arguments");
		await Throws<System.ComponentModel.InvalidEnumArgumentException>(() => plugin.OnBotCommand(bot, (EAccess) 254, "acv", ["acv"]), "invalid access validation");
		await Throws<ArgumentException>(() => plugin.OnBotCommand(bot, EAccess.Master, "", ["acv"]), "empty message validation");
		await Throws<ArgumentNullException>(() => plugin.OnBotCommand(bot, EAccess.Master, "acv", []), "empty arguments validation");
		await Throws<ArgumentNullException>(() => plugin.OnBotCommand(bot, EAccess.Master, "acv", null!), "null arguments validation");
		await Throws<ArgumentOutOfRangeException>(() => plugin.OnBotCommand(bot, EAccess.Master, "acv", ["acv"], 1), "invalid Steam ID validation");
		await Throws<ArgumentNullException>(() => plugin.OnBotCommand(null!, EAccess.Master, "acv", ["acv"]), "null bot validation");
		Console.WriteLine($"PASS: {Passed} tests; no live Steam requests");
	}

	private static void TestLocalization() {
		CultureInfo originalCulture = CultureInfo.CurrentUICulture;

		try {
			foreach (string cultureName in new[] { "en", "fr-FR" }) {
				CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(cultureName);

				foreach (string message in new[] {
					PluginLocale.Strings.SessionUnavailable,
					PluginLocale.Strings.AvatarNotConfirmed,
					PluginLocale.Strings.AvatarTimedOut,
					PluginLocale.Strings.AvatarFailed,
					PluginLocale.Strings.AvatarTooLarge,
					PluginLocale.Strings.AvatarInvalidImage
				}) {
					Check(!string.IsNullOrWhiteSpace(message), "plugin resource resolves with culture fallback");
				}

				Check(PluginLocale.Strings.FormatAvatarFailed(nameof(HttpRequestException)) == string.Format(CultureInfo.CurrentCulture, PluginLocale.Strings.AvatarFailed, nameof(HttpRequestException)), "localized failure format");

				try {
					AvatarChangerPlugin.GetImageType([]);
					throw new InvalidOperationException("FAIL: invalid image accepted");
				} catch (InvalidDataException e) {
					Check(e.Message == PluginLocale.Strings.AvatarInvalidImage, "localized image validation");
				}
			}
		} finally {
			CultureInfo.CurrentUICulture = originalCulture;
		}
	}

	private static void Check(bool result, string name) {
		if (!result) {
			throw new InvalidOperationException($"FAIL: {name}");
		}

		Passed++;
	}

	private static async Task Throws<T>(Func<Task> action, string name) where T : Exception {
		try {
			await action();
		} catch (T) {
			Passed++;
			return;
		}

		throw new InvalidOperationException($"FAIL: {name}");
	}

	private sealed class FakeHandler(Func<HttpResponseMessage> response) : HttpMessageHandler {
		protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
			cancellationToken.ThrowIfCancellationRequested();
			Check(request.Headers.Authorization == null && !request.Headers.Contains("Cookie"), "download has no credentials");
			return Task.FromResult(response());
		}
	}

	private sealed class NonSeekableStream(byte[] data) : MemoryStream(data) {
		public override bool CanSeek => false;
	}
}
