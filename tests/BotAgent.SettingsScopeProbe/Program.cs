using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Ops;
using BotAgent.Services.Ports;
using BotAgent.Services.Settings;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Services.Reply;
using BotAgent.Services.OneBot;
using Microsoft.Data.Sqlite;

var root = Path.Combine(Path.GetTempPath(), "bot-settings-scope-probe-" + Guid.NewGuid().ToString("N"));
Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
var failures = 0;
try
{
    // Upgrade a synthetic legacy database rather than relying only on a fresh schema.
    Directory.CreateDirectory(AppPaths.DataDir);
    using (var legacy = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = AppDatabase.FilePath }.ToString()))
    {
        legacy.Open();
        using var command = legacy.CreateCommand();
        command.CommandText = "CREATE TABLE own_messages(message_id INTEGER PRIMARY KEY, text TEXT NOT NULL, at_unix INTEGER NOT NULL); " +
            "INSERT INTO own_messages VALUES(10001, 'synthetic legacy', 0);";
        command.ExecuteNonQuery();
    }
    var oldJson = "[{\"id\":10004,\"text\":\"synthetic legacy JSON\",\"at\":\"1970-01-01T00:00:00Z\"}]";
    File.WriteAllText(Path.Combine(AppPaths.DataDir, "own-messages.json"), oldJson);
    AppDatabase.Initialize();
    Check("save failure propagates and rolls settings back", () =>
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { AiDesire = 17 });
        var threw = false;
        try
        {
            store.Save(new AppSettings { AiDesire = 29 },
                new AuditEvent("", "actor-test", "tenant-test", "synthetic", "2.1"), new AuditLogStore());
        }
        catch (ArgumentException) { threw = true; }
        Require(threw, "failed audit/save returned success");
        Require(store.Load().AiDesire == 17, "failed transaction changed stored settings");
    });
    Check("failed hot reload leaves runtime unpublished", () =>
    {
        var original = new AppSettings { AiDesire = 17 };
        var box = new SettingsBox(original);
        var repo = new ThrowingSettingsRepository();
        var reload = new SettingsHotReload(box, repo, null!, null!, null!, null!, null!, null!, null!, null!, null!);
        Exception? observed = null;
        try { reload.ApplyRuntimeSettings(s => s.AiDesire = 29); }
        catch (Exception ex) { observed = ex; }
        Require(ReferenceEquals(box.Current, original), "failed save published runtime snapshot");
        Require(observed is IOException && repo.Attempts == 1, "persistence must fail before runtime rebuild");
    });
    Check("recalled survives real SQLite record roundtrip", () =>
    {
        var conversation = new BotConversation { SourceKey = "group:10001", Kind = ConversationKind.GroupChat };
        conversation.Append(new ChatMessage { Role = MessageRole.Peer, Timestamp = DateTimeOffset.UnixEpoch, Text = "synthetic recalled", Recalled = true, QqMessageId = 10001 });
        var store = new ConversationStore();
        store.RequestSave(new[] { conversation.ToRecord() });
        var record = store.LoadAsync().Single(r => r.SourceKey == conversation.SourceKey);
        var restored = BotConversation.FromRecord(record);
        Require(restored.Messages.Single().Recalled, "recalled flag lost after record/database/reload");
        Require(restored.Messages.Single().Text == "synthetic recalled", "recalled content must be retained");
    });
    Check("legacy own-message identity fails closed and data remains", () =>
    {
        var store = new OwnMessageStore();
        var ledger = new OwnMessageLedger(store, _ => { });
        ledger.EnsureLoaded();
        Require(!ledger.TryGet(10001, out _), "bare native id was treated as a scoped identity");
        Require(store.LoadRecent(200).Any(r => r.Id == 10001), "legacy data deleted");
        Require(!ledger.TryGet("group:10004", 10004, out _), "legacy JSON row was assigned a guessed scope");
        Require(store.LoadRecent(200).Any(r => r.Id == 10004), "legacy JSON data not retained/imported");
        var archive = Path.Combine(root, "legacy-json", "data", "own-messages.json");
        Require(File.Exists(archive) && File.ReadAllText(archive) == oldJson, "legacy JSON archive lost original bytes");
        store.LoadRecentScoped(200);
        AppDatabase.Initialize();
        Require(store.LoadRecent(200).Count == 2, "repeat initialization/import duplicated or lost legacy data");
    });
    Check("full scoped identity survives restart without any cross-scope hit", () =>
    {
        var store = new OwnMessageStore();
        var original = new ConversationId(PlatformId.QqPrivate, "account-test", ConversationKind.GroupChat, "target-test");
        var scopes = new[]
        {
            original,
            original with { PlatformId = PlatformId.QqOfficial },
            original with { AccountScope = "account-other" },
            original with { AccountScope = "Account-test" },
            original with { NativeTargetId = "target-other" },
            original with { Kind = ConversationKind.PrivateChat },
            original with { ThreadId = "thread-test" },
            original with { ThreadId = "thread-other" }
        };
        var ledger = new OwnMessageLedger(store, _ => { });
        for (var i = 0; i < scopes.Length; i++)
            ledger.Remember(new MessageRef(scopes[i], "Native-Case-10001"), "synthetic-" + i, DateTimeOffset.UnixEpoch.AddSeconds(i));
        var reloaded = new OwnMessageLedger(new OwnMessageStore(), _ => { });
        for (var i = 0; i < scopes.Length; i++)
        {
            Require(reloaded.TryGet(new MessageRef(scopes[i], "Native-Case-10001"), out var hit) && hit.Text == "synthetic-" + i,
                "scoped identity collided or failed reload at index " + i);
            Require(!reloaded.TryGet(new MessageRef(scopes[i], "native-case-10001"), out _), "native id case was changed");
        }
        Require(!reloaded.TryGet(new MessageRef(original with { AccountScope = "missing-test" }, "Native-Case-10001"), out _),
            "unknown account read an existing message");
        Require(!reloaded.TryGet("malformed-scope", 10001, out _), "malformed scope guessed a default identity");
        Require(!reloaded.TryGet("group:10001", 10001, out _), "retained ambiguous legacy row became scoped");
        ledger.Remember("group:10002", new SendResult(true, 10001), "synthetic explicit legacy-scope");
        Require(new OwnMessageLedger(store, _ => { }).TryGet("group:10002", 10001, out var legacyHit)
            && legacyHit.Text == "synthetic explicit legacy-scope", "explicit legacy conversation key did not canonicalize");
    });
    Check("real cadence sender preserves supplied account and thread when remembering sends", () =>
    {
        var store = new OwnMessageStore();
        var ledger = new OwnMessageLedger(store, _ => { });
        var source = new SyntheticChatSource();
        var sender = new PlainSender(new SettingsBox(new AppSettings { SplitReplies = false }), source,
            null!, null!, ledger, _ => { }, new TurnTraceStore());
        var conversation = new ConversationId(PlatformId.QqPrivate, "sender-account-test",
            ConversationKind.GroupChat, "10005", "thread-test");
        var key = ConversationIdCodec.EncodeStructured(conversation);
        var report = sender.SendWithCadenceAsync(key, true, 10005, "synthetic sender", null).GetAwaiter().GetResult();
        Require(report.AnySent && source.Sends == 1 && source.LastTarget == 10005, "synthetic transport did not send");
        var reloaded = new OwnMessageLedger(store, _ => { });
        Require(reloaded.TryGet(key, 10005, out var hit) && hit.Text == "synthetic sender",
            "real sender lost account/thread scope while writing the ledger");
        foreach (var other in new[] { conversation with { ThreadId = "thread-other" },
            conversation with { AccountScope = "sender-account-other" }, conversation with { NativeTargetId = "native-target-test" } })
        {
            var otherKey = ConversationIdCodec.EncodeStructured(other);
            sender.SendWithCadenceAsync(otherKey, true, 10005, "synthetic other " + otherKey, null).GetAwaiter().GetResult();
            Require(new OwnMessageLedger(store, _ => { }).TryGet(otherKey, 10005, out var otherHit)
                && otherHit.Text == "synthetic other " + otherKey, "same native id collided in real sender scope");
        }
        Require(new OwnMessageLedger(store, _ => { }).TryGet(key, 10005, out hit) && hit.Text == "synthetic sender",
            "real sender overwrote first scope with a sibling account/thread/target");
        var invalid = sender.SendWithCadenceAsync("malformed-scope", true, 10005, "synthetic ignored", null).GetAwaiter().GetResult();
        Require(!invalid.AnySent && source.Sends == 4 && source.LastTarget == 10005,
            "invalid source scope sent anyway or fullscope changed transport routing");
    });
    Check("invalid kind and incomplete account are rejected rather than assigned a scope", () =>
    {
        var store = new OwnMessageStore();
        var original = new ConversationId(PlatformId.QqPrivate, "account-test", ConversationKind.GroupChat, "target-test");
        foreach (var bad in new[] { original with { Kind = (ConversationKind)999 }, original with { AccountScope = "" } })
        {
            var rejected = false;
            try { store.Upsert(new MessageRef(bad, "invalid-test"), "synthetic", DateTimeOffset.UnixEpoch); }
            catch (ArgumentException) { rejected = true; }
            Require(rejected, "invalid scope was silently assigned a valid default");
        }
    });
    Check("unsupported audit chain fails before any settings write", () =>
    {
        var store = new SettingsStore();
        store.Save(new AppSettings { AiDesire = 17 });
        var audit = new FakeAuditChain();
        foreach (var chain in new IAuditChain?[] { null, audit })
        {
            var rejected = false;
            try { store.Save(new AppSettings { AiDesire = 29 }, Event("unsupported"), chain); }
            catch (InvalidOperationException) { rejected = true; }
            Require(rejected && store.Load().AiDesire == 17, "non-atomic audit was silently accepted");
        }
        Require(audit.Appends == 0, "external audit side effect ran before transaction rejection");
    });
    Check("mutation failure has no persistence or publication side effects", () =>
    {
        var original = new AppSettings { AiDesire = 17 };
        var box = new SettingsBox(original);
        var persisted = false;
        var published = false;
        try { box.ApplyPersisted(s => { s.AiDesire = 29; throw new ArgumentException("synthetic"); },
            _ => persisted = true, _ => published = true); }
        catch (ArgumentException) { }
        Require(!persisted && !published && ReferenceEquals(box.Current, original), "failed mutation escaped staging");
    });
    Check("concurrent settings writers persist publish and rebuild in one order", () =>
    {
        var store = new SettingsStore();
        var box = new SettingsBox(new AppSettings { AiDesire = 0, MaxMessagesPerConversation = 100 });
        var published = 0;
        using var start = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 20).Select(i => Task.Run(() =>
        {
            start.Wait();
            box.ApplyPersisted(s =>
            {
                if (i % 2 == 0) s.AiDesire++;
                else s.MaxMessagesPerConversation++;
            }, store.Save, next =>
            {
                var disk = store.Load();
                Require(ReferenceEquals(box.Current, next), "a later writer raced runtime rebuild");
                Require(disk.AiDesire == next.AiDesire && disk.MaxMessagesPerConversation == next.MaxMessagesPerConversation,
                    "disk version differs from published candidate");
                published++;
            });
        })).ToArray();
        start.Set();
        Require(Task.WaitAll(tasks, TimeSpan.FromSeconds(15)), "settings writers timed out");
        Require(published == 20 && box.Current.AiDesire == 10 && box.Current.MaxMessagesPerConversation == 110,
            "concurrent writers lost a sibling update");
    });
    Check("postcommit callback failure is visible but committed settings stay published", () =>
    {
        var store = new SettingsStore();
        var box = new SettingsBox(new AppSettings { AiDesire = 17 });
        var threw = false;
        try { box.ApplyPersisted(s => s.AiDesire = 29, store.Save, _ => throw new IOException("synthetic rebuild failure")); }
        catch (IOException) { threw = true; }
        Require(threw && box.Current.AiDesire == 29 && store.Load().AiDesire == 29,
            "postcommit failure was hidden or falsely rolled back");
    });
    Check("ordinary audit and settings audit serialize without lock inversion or chain forks", () =>
    {
        var store = new SettingsStore();
        var audit = new AuditLogStore();
        var before = audit.Verify().CheckedCount;
        using var start = new ManualResetEventSlim(false);
        var regular = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 30; i++) audit.Append(Event("ordinary-" + i));
        });
        var settings = Task.Run(() =>
        {
            start.Wait();
            for (var i = 0; i < 30; i++) store.Save(new AppSettings { AiDesire = i }, Event("settings-" + i), audit);
        });
        start.Set();
        Require(Task.WaitAll(new[] { regular, settings }, TimeSpan.FromSeconds(15)), "audit writers timed out");
        var result = audit.Verify();
        Require(result.Valid && result.CheckedCount == before + 60, "concurrent audit chain forked or lost events");
        Require(store.Load().AiDesire == 29, "settings audit did not commit its settings");
    });
    Check("legacy scoped retention and pruning remain independent", () =>
    {
        var store = new OwnMessageStore();
        store.Upsert(10003, "synthetic ignored bare-id write", DateTimeOffset.UnixEpoch);
        Require(!store.LoadRecent(200).Any(r => r.Id == 10003), "bare-id write created ambiguous new data");
        store.PruneTo(2);
        Require(store.LoadRecentScoped(200).Count == 2, "scoped pruning failed");
        Require(store.LoadRecent(200).Any(r => r.Id == 10001), "scoped pruning deleted retained legacy data");
    });
}
finally
{
    SqliteConnection.ClearAllPools();
    if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
}
Console.WriteLine($"SettingsScopeProbe failures={failures}");
return failures == 0 ? 0 : 1;

void Check(string name, Action action)
{
    try { action(); Console.WriteLine("PASS " + name); }
    catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); }
}
static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static AuditEvent Event(string detail) => new("synthetic", "actor-test", "tenant-test", detail, "2.1");

sealed class FakeAuditChain : IAuditChain
{
    public int Appends { get; private set; }
    public void Append(AuditEvent auditEvent) => Appends++;
    public AuditVerification Verify() => new(true, null, null, Appends);
}

sealed class SyntheticChatSource : IQqChatSource
{
    public event Action<QqChatMessage>? MessageReceived { add { } remove { } }
    public event Action<QqPokeEvent>? Poked { add { } remove { } }
    public event Action<QqRecallEvent>? MessageRecalled { add { } remove { } }
    public event Action<bool>? ConnectionChanged { add { } remove { } }
    public bool IsConnected => true;
    public int Sends { get; private set; }
    public long LastTarget { get; private set; }
    public Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default,
        long? replyToMessageId = null, bool directAddress = false)
    {
        Sends++;
        LastTarget = targetId;
        return Task.FromResult(new SendResult(true, 10005));
    }
    public Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
        => Task.FromResult<(string?, long)>((null, 0));
    public Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
        => Task.FromResult(false);
    public Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default) => Task.FromResult<string?>(null);
}

sealed class ThrowingSettingsRepository : ISettingsRepository
{
    public int Attempts { get; private set; }
    public string FilePath => "synthetic";
    public bool ExistsOnDisk => false;
    public bool HasStoredSettings() => false;
    public AppSettings Load() => new();
    public void Save(AppSettings settings) { Attempts++; throw new IOException("synthetic save failure"); }
}
