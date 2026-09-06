using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

static class WorkshopChangeNotes
{
    const ulong Item = 0000000000;
    static readonly Uri Community = new("https://steamcommunity.com/");
    static readonly string[] FormFields = ["change_description","id","language","sessionid","timestamp"];
    public sealed record Note(string Code, int Language, string Text);
    public static List<Note> Load(string root)
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        var hashes=json.RootElement.GetProperty("localizedNotes");
        var notes=new List<Note>();
        foreach (var (code,language) in new[] { ("en",0),("zh-CN",6) })
        {
            var bytes=File.ReadAllBytes(Path.Combine(root,$"release-notes.{code}.md"));
            if (Convert.ToHexString(SHA256.HashData(bytes)) != hashes.GetProperty(code).GetString())
                throw new PublisherException($"Localized change-note checksum mismatch: {code}.");
            var text=Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').TrimEnd();
            if (string.IsNullOrWhiteSpace(text) || text.Contains(@"\n"))
                throw new PublisherException($"Invalid localized change note: {code}.");
            notes.Add(new(code,language,text));
        }
        return notes;
    }
    public static void Validate(string root)
    {
        _=Load(root);
        var form=CreateForm("session",1,0,"note");
        if (!form.Select(pair=>pair.Key).Order().SequenceEqual(FormFields))
            throw new PublisherException("Localized change-note form validation failed.");
    }
    public static async Task<uint> Latest(PublishedFile service)
    {
        var response=await service.GetChangeHistory(new CPublishedFile_GetChangeHistory_Request
        { publishedfileid=Item, language=0, count=1 });
        if (response.Result != EResult.OK) throw new PublisherException("Cannot read Workshop change history.");
        return response.Body.changes.Count == 0 ? 0 : response.Body.changes[0].timestamp;
    }
    public static async Task<CPublishedFile_GetChangeHistoryEntry_Response> Read(PublishedFile service,uint timestamp,int language)
    {
        var response=await service.GetChangeHistoryEntry(new CPublishedFile_GetChangeHistoryEntry_Request
        { publishedfileid=Item, timestamp=timestamp, language=language });
        if (response.Result != EResult.OK) throw new PublisherException("Cannot read localized change note.");
        return response.Body;
    }
    public static async Task Prepare(PublishedFile service,string root)
    {
        _=Load(root);
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        var tag=manifest.RootElement.GetProperty("tag").GetString();
        await File.WriteAllTextAsync(Path.Combine(root,"note-marker.json"),JsonSerializer.Serialize(new { tag, timestamp=await Latest(service) }));
        Console.WriteLine("Recorded pre-upload change-history marker. No notes changed.");
    }
    public static async Task Publish(SteamClient client,string refreshToken,string root)
    {
        var service=client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
        var markerPath=Path.Combine(root,"note-marker.json");
        using var marker=JsonDocument.Parse(File.ReadAllText(markerPath));
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"manifest.json")));
        if (marker.RootElement.GetProperty("tag").GetString() != manifest.RootElement.GetProperty("tag").GetString())
            throw new PublisherException("Change-note marker does not match the release tag.");
        var previous=marker.RootElement.GetProperty("timestamp").GetUInt32();
        uint timestamp=0;
        for (var attempt=0;attempt<10;attempt++)
        {
            timestamp=await Latest(service);
            if (timestamp>previous) break;
            await Task.Delay(2000);
        }
        if (timestamp<=previous) throw new PublisherException("Steam did not create a new change-history entry for this upload.");
        using var http=await CreateCommunityClient(client,refreshToken);
        foreach (var note in Load(root)) await Update(http,timestamp,note);
        for (var attempt=0;attempt<5;attempt++)
        {
            try { await Verify(service,root,timestamp); return; }
            catch (PublisherException) when (attempt<4) { await Task.Delay(2000); }
        }
    }
    public static async Task Repair(SteamClient client,string refreshToken,string root)
    {
        var service=client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>();
        var timestamp=await Latest(service);
        if (timestamp == 0) throw new PublisherException("Workshop change history is empty.");
        using var http=await CreateCommunityClient(client,refreshToken);
        foreach (var note in Load(root)) await Update(http,timestamp,note);
        await Verify(service,root,timestamp);
    }
    public static async Task CheckEditorContract(SteamClient client,string refreshToken)
    {
        using var http=await CreateCommunityClient(client,refreshToken);
        var timestamp=await Latest(client.GetHandler<SteamUnifiedMessages>()!.CreateService<PublishedFile>());
        if (timestamp == 0) throw new PublisherException("Workshop change history is empty.");
        var body=await http.GetStringAsync($"sharedfiles/editchangelogentry/{Item}/{timestamp}/?language=0");
        var endpoint=body.IndexOf("ajaxsetchangelogentry",StringComparison.Ordinal);
        if (endpoint < 0) throw new PublisherException("Workshop change-note editor contract is unavailable.");
        var start=body.LastIndexOf("<script",endpoint,StringComparison.OrdinalIgnoreCase);
        var end=body.IndexOf("</script>",endpoint,StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0 || end-start > 20000) throw new PublisherException("Workshop change-note editor script is invalid.");
        var script=body.Substring(start,end-start);
        var fields=Regex.Matches(script,"['\\\"](?<key>[a-z_]{1,40})['\\\"]\\s*:")
            .Select(match=>match.Groups["key"].Value).Distinct(StringComparer.Ordinal).Order().ToArray();
        if (fields.Length == 0) throw new PublisherException("Workshop change-note editor fields were not found.");
        if (!fields.SequenceEqual(FormFields)) throw new PublisherException("Workshop change-note editor fields changed.");
        Console.WriteLine("PASS: Workshop change-note editor contract verified without exposing session data.");
    }
    public static async Task Verify(PublishedFile service,string root,uint timestamp=0)
    {
        if (timestamp == 0) timestamp=await Latest(service);
        foreach (var note in Load(root))
        {
            var actual=await Read(service,timestamp,note.Language);
            if (actual.language != note.Language || Normalize(actual.change_description) != Normalize(note.Text))
                throw new PublisherException($"Localized change-note verification failed: {note.Code}, timestamp {timestamp}.");
            Console.WriteLine($"Verified change note: {note.Code}, timestamp {timestamp}.");
        }
    }
    static string Normalize(string text) => text.Replace("\r\n","\n").TrimEnd();
    static async Task Update(HttpClient http,uint timestamp,Note note)
    {
        var page=await http.GetStringAsync($"sharedfiles/editchangelogentry/{Item}/{timestamp}/?language={note.Language}");
        var match=Regex.Match(page,"name=[\"']sessionid[\"'][^>]*value=[\"'](?<value>[^\"']+)[\"']",RegexOptions.IgnoreCase);
        if (!match.Success) throw new PublisherException("Workshop editor session could not be established.");
        var session=System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
        using var response=await http.PostAsync("sharedfiles/ajaxsetchangelogentry",new FormUrlEncodedContent(CreateForm(session,timestamp,note.Language,note.Text)));
        if (!response.IsSuccessStatusCode) throw new PublisherException($"Localized change-note update rejected: {note.Code}.");
        Console.WriteLine($"Submitted localized change note: {note.Code}, timestamp {timestamp}.");
    }
    static Dictionary<string,string> CreateForm(string session,uint timestamp,int language,string text) => new(StringComparer.Ordinal)
    {
        ["id"]=Item.ToString(), ["timestamp"]=timestamp.ToString(), ["sessionid"]=session,
        ["change_description"]=text, ["language"]=language.ToString()
    };
    static async Task<HttpClient> CreateCommunityClient(SteamClient client,string refreshToken)
    {
        var steamId=client.SteamID ?? throw new PublisherException("Steam web session has no account identity.");
        AccessTokenGenerateResult access=await client.Authentication.GenerateAccessTokenForAppAsync(steamId,refreshToken);
        var handler=new HttpClientHandler { CookieContainer=new System.Net.CookieContainer(),AllowAutoRedirect=true };
        handler.CookieContainer.Add(new System.Net.Cookie("steamLoginSecure",Uri.EscapeDataString(steamId.ConvertToUInt64()+"||"+access.AccessToken),"/","steamcommunity.com") { Secure=true,HttpOnly=true });
        return new HttpClient(handler) { BaseAddress=Community,Timeout=TimeSpan.FromSeconds(30) };
    }
}
