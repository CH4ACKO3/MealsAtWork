using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

return await Run(args);

static async Task<int> Run(string[] args)
{
    try
    {
        if (args.Length == 2 && args[0] == "auth")
        {
            if (!Path.IsPathFullyQualified(args[1]) || File.Exists(args[1]))
                throw new PublisherException("Use a new absolute token-file path outside the repository.");
            Console.Write("Steam account: ");
            var account = Console.ReadLine() ?? "";
            Console.Write("Password: ");
            var password = new StringBuilder();
            while (true)
            {
                var key = Console.ReadKey(true);
                if (key.Key == ConsoleKey.Enter) break;
                if (key.Key == ConsoleKey.Backspace) { if (password.Length > 0) password.Length--; }
                else if (!char.IsControl(key.KeyChar)) password.Append(key.KeyChar);
            }
            Console.WriteLine();
            using var session = new Session();
            await session.Connect();
            var auth = await session.Client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = account, Password = password.ToString(), IsPersistentSession = true,
                Authenticator = new UserConsoleAuthenticator()
            });
            password.Clear();
            var result = await auth.PollingWaitForResultAsync();
            await File.WriteAllTextAsync(args[1], result.RefreshToken, new UTF8Encoding(false));
            Console.WriteLine("Saved refresh token. Upload it as STEAM_REFRESH_TOKEN, then remove the local file.");
            return 0;
        }
        if (args.Length != 2 || (args[0] != "validate" && args[0] != "publish" && args[0] != "check" &&
            args[0] != "prepare-notes" && args[0] != "publish-notes" && args[0] != "repair-notes" && args[0] != "verify-notes"))
            throw new PublisherException("Usage: validate|check|publish|prepare-notes|publish-notes|repair-notes|verify-notes <artifact-root>, or auth <new-token-file>.");
        var descriptions = LoadDescriptions(args[1], args[0] is "publish" or "prepare-notes" or "publish-notes" or "repair-notes");
        if (args[0] == "validate")
        {
            // Round-trip the actual protobuf requests used for each language without a Steam connection.
            foreach (var item in descriptions)
            {
                var request = CreateUpdate(item, null);
                using var stream = new MemoryStream();
                ProtoBuf.Serializer.Serialize(stream, request);
                stream.Position = 0;
                var copy = ProtoBuf.Serializer.Deserialize<CPublishedFile_Update_Request>(stream);
                if (copy.language != item.Language || copy.file_description != item.Text || copy.publishedfileid != 0000000000UL)
                    throw new PublisherException("Localized request serialization failed.");
            }
            WorkshopChangeNotes.Validate(args[1]);
            Console.WriteLine("PASS: pinned English/Chinese descriptions, hashes and localized update requests (no login).");
            return 0;
        }
        var username = Secret("STEAM_USERNAME");
        var refreshToken = Secret("STEAM_REFRESH_TOKEN");
        using var live = new Session();
        await live.Connect();
        await live.Login(username, refreshToken);
        var service = live.Client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
        // Read both languages and verify ownership before sending any description writes.
        var previous = new List<PublishedFileDetails>();
        foreach (var item in descriptions) previous.Add(await Read(service, item.Language, live.Client.SteamID!.ConvertToUInt64()));
        if (args[0] == "prepare-notes")
        {
            await WorkshopChangeNotes.Prepare(service,args[1]);
            return 0;
        }
        if (args[0] == "publish-notes")
        {
            await WorkshopChangeNotes.Publish(live.Client,refreshToken,args[1]);
            return 0;
        }
        if (args[0] == "repair-notes")
        {
            await WorkshopChangeNotes.Repair(live.Client,refreshToken,args[1]);
            return 0;
        }
        if (args[0] == "verify-notes")
        {
            await WorkshopChangeNotes.Verify(service,args[1]);
            return 0;
        }
        if (args[0] == "check")
        {
            await WorkshopChangeNotes.CheckEditorContract(live.Client, refreshToken);
            Console.WriteLine("PASS: authenticated ownership and bilingual read access. No descriptions changed.");
            return 0;
        }
        for (var i = 0; i < descriptions.Count; i++)
        {
            var item = descriptions[i];
            if (Normalize(previous[i].file_description) != Normalize(item.Text))
            {
                var update = await service.Update(CreateUpdate(item, previous[i]));
                if (update.Result != EResult.OK) throw new PublisherException($"Description update rejected for language {item.Language}: {update.Result}.");
            }
            PublishedFileDetails after = previous[i];
            for (var attempt = 0; attempt < 5; attempt++)
            {
                after = await Read(service, item.Language, live.Client.SteamID!.ConvertToUInt64());
                if (Normalize(after.file_description) == Normalize(item.Text)) break;
                await Task.Delay(2000);
            }
            if (after.language != item.Language || Normalize(after.file_description) != Normalize(item.Text) ||
                after.title != previous[i].title || after.visibility != previous[i].visibility ||
                !after.tags.Select(t => t.tag).Order().SequenceEqual(previous[i].tags.Select(t => t.tag).Order()))
                throw new PublisherException($"Post-update verification failed for language {item.Language}; inspect Workshop before retrying.");
            Console.WriteLine($"Verified Workshop description: {(item.Language == 0 ? "english" : "schinese")}.");
        }
        return 0;
    }
    catch (Exception ex)
    {
        // SDK exception messages can carry authentication responses; expose only our controlled messages.
        Console.Error.WriteLine(ex is PublisherException ? ex.Message : $"Steam Workshop operation failed ({ex.GetType().Name}); verify authorization locally.");
        return 1;
    }
}

static string Secret(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
    ? value : throw new PublisherException($"Missing environment secret: {key}.");
static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd();
static List<Description> LoadDescriptions(string root, bool requireTag)
{
    using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "manifest.json")));
    var m = doc.RootElement;
    if (m.GetProperty("appid").GetString() != "294100" || m.GetProperty("publishedfileid").GetString() != "0000000000" ||
        (requireTag && m.GetProperty("tag").GetString() != "v" + m.GetProperty("version").GetString()))
        throw new PublisherException("Manifest does not identify the MAW release.");
    var result = new List<Description>();
    foreach (var (code, language) in new[] { ("en", 0), ("zh-CN", 6) })
    {
        var bytes = File.ReadAllBytes(Path.Combine(root, $"Description.{code}.bbcode"));
        if (Convert.ToHexString(SHA256.HashData(bytes)) != m.GetProperty("descriptions").GetProperty(code).GetString())
            throw new PublisherException($"Description checksum mismatch: {code}.");
        var text = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text) || Encoding.UTF8.GetByteCount(text) >= 8000)
            throw new PublisherException($"Invalid description length: {code}.");
        result.Add(new(language, text));
    }
    return result;
}
static CPublishedFile_Update_Request CreateUpdate(Description item, PublishedFileDetails? previous)
{
    var request = new CPublishedFile_Update_Request
    { appid = 294100, publishedfileid = 0000000000, language = item.Language, file_description = item.Text };
    if (previous != null)
    {
        request.title = previous.title;
        request.visibility = previous.visibility;
        request.tags.AddRange(previous.tags.Select(t => t.tag));
    }
    return request;
}
static async Task<PublishedFileDetails> Read(PublishedFile service, int language, ulong owner)
{
    var request = new CPublishedFile_GetDetails_Request
    { appid = 294100, language = language, includetags = true, short_description = false, strip_description_bbcode = false };
    request.publishedfileids.Add(0000000000);
    var response = await service.GetDetails(request);
    if (response.Result != EResult.OK || response.Body.publishedfiledetails.Count != 1)
        throw new PublisherException("Cannot read the existing Workshop item.");
    var item = response.Body.publishedfiledetails[0];
    if (item.result != (uint)EResult.OK || item.publishedfileid != 0000000000UL || item.consumer_appid != 294100 || item.creator != owner)
        throw new PublisherException("Workshop identity/ownership check failed.");
    return item;
}
sealed record Description(int Language, string Text);
sealed class Session : IDisposable
{
    public SteamClient Client { get; } = new();
    readonly CancellationTokenSource stop = new();
    readonly TaskCompletionSource connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource loggedIn = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly CallbackManager manager;
    Task? pump;
    public Session()
    {
        manager = new(Client);
        manager.Subscribe<SteamClient.ConnectedCallback>(_ => connected.TrySetResult());
        manager.Subscribe<SteamClient.DisconnectedCallback>(_ =>
        {
            connected.TrySetException(new PublisherException("Steam disconnected."));
            loggedIn.TrySetException(new PublisherException("Steam disconnected."));
        });
        manager.Subscribe<SteamUser.LoggedOnCallback>(c =>
        {
            if (c.Result == EResult.OK) loggedIn.TrySetResult();
            else loggedIn.TrySetException(new PublisherException($"Steam login failed: {c.Result}. Refresh authorization locally."));
        });
    }
    public async Task Connect()
    {
        pump = Task.Run(() => { while (!stop.IsCancellationRequested) manager.RunWaitCallbacks(TimeSpan.FromMilliseconds(100)); });
        Client.Connect();
        await connected.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    public async Task Login(string account, string token)
    {
        Client.GetHandler<SteamUser>()!.LogOn(new SteamUser.LogOnDetails
        { Username = account, AccessToken = token, ShouldRememberPassword = true });
        await loggedIn.Task.WaitAsync(TimeSpan.FromSeconds(45));
    }
    public void Dispose() { Client.Disconnect(); stop.Cancel(); pump?.Wait(TimeSpan.FromSeconds(2)); stop.Dispose(); }
}

sealed class PublisherException(string message) : Exception(message);
