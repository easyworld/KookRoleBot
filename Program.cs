using Kook;
using Kook.WebSocket;
using KookRoleBot;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KookHttpException = Kook.Net.HttpException;

const string configPath = "config.json";

if (!File.Exists(configPath))
{
    var defaultConfig = new BotConfig(Token: "在此填写你的 Kook Bot Token", DatabasePath: "kookbot.db", AdminRoleName: "管理员");
    await File.WriteAllTextAsync(configPath,
        JsonSerializer.Serialize(defaultConfig, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
    Console.WriteLine($"已生成配置文件 {configPath}，请填写 Kook 机器人的 Bot Token 后重新启动。");
    return;
}

var config = JsonSerializer.Deserialize<BotConfig>(await File.ReadAllTextAsync(configPath))
    ?? throw new InvalidOperationException("Failed to load config.json");

var kookConfig = new KookSocketConfig
{
    AlwaysDownloadUsers = true
};

using var client = new KookSocketClient(kookConfig);

await using var db = new BotDatabase(config.DatabasePath ?? "kookbot.db");
await db.InitializeAsync();

client.Log += log =>
{
    Console.WriteLine($"[{log.Severity}] {log.Source}: {log.Message ?? log.Exception?.Message}");
    return Task.CompletedTask;
};

client.MessageReceived += (message, guildUser, textChannel) => HandleMessageAsync(client, db, config, message, guildUser, textChannel);

await client.LoginAsync(TokenType.Bot, config.Token);
await client.StartAsync();

Console.WriteLine("Bot started. Press Ctrl+C to exit.");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = false; cts.Cancel(); };

try
{
    using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
    while (await timer.WaitForNextTickAsync(cts.Token))
    {
        await CheckExpiredRolesAsync(client, db);
    }
}
catch (OperationCanceledException) { }

static async Task HandleMessageAsync(KookSocketClient client, BotDatabase db, BotConfig config, SocketMessage message,
    SocketGuildUser sender, SocketTextChannel channel)
{
    try
    {
    if (message.Author?.Id == client.CurrentUser?.Id) return;

    var guild = channel.Guild;
    if (guild == null) return;

    var adminRoleName = config.AdminRoleName ?? "管理员";
    if (!sender.Roles.Any(r => r.Name == adminRoleName)) return;

    var currentUserId = client.CurrentUser?.Id ?? 0;
    if (!message.MentionedUserIds.Contains(currentUserId)) return;

    var targetUserIds = message.MentionedUserIds
        .Where(id => id != currentUserId)
        .ToList();

    if (targetUserIds.Count == 0)
    {
        await channel.SendTextAsync("请 @mention 需要添加角色的用户。格式：@机器人 @用户 角色名 +时长", quote: new MessageReference(message.Id));
        return;
    }

    var content = Regex.Replace(message.Content, @"\(met\)[^()]*\(met\)", "").Trim();

    var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2)
    {
        await channel.SendTextAsync("格式错误。正确格式：@机器人 @用户 角色名 +时长（如 +1d）", quote: new MessageReference(message.Id));
        return;
    }

    var roleName = parts[0];
    var durationStr = string.Join(" ", parts[1..]);
    var duration = ParseDuration(durationStr);
    if (duration == null)
    {
        await channel.SendTextAsync("时长格式错误。请使用 +Nd（如 +1d, +7d）", quote: new MessageReference(message.Id));
        return;
    }

    var role = guild.Roles.FirstOrDefault(r => r.Name == roleName);
    if (role == null)
    {
        await channel.SendTextAsync($"服务器中不存在角色 \"{roleName}\"", quote: new MessageReference(message.Id));
        return;
    }

    var replies = new List<string>();
    foreach (var userId in targetUserIds)
    {
        var guildUser = guild.GetUser(userId);
        if (guildUser == null)
        {
            replies.Add($"用户 ID:{userId} 不在服务器中");
            continue;
        }

        var alreadyHasRole = guildUser.Roles.Any(r => r.Id == role.Id);

        if (!alreadyHasRole)
        {
            await guildUser.AddRoleAsync(role);
            Console.WriteLine($"[Role] 授予角色 \"{roleName}\" 给 {guildUser.Username}#{guildUser.IdentifyNumber}");
        }

        var existing = await db.GetExpirationAsync(guild.Id, guildUser.Id, role.Id);
        DateTime newExpiration;
        if (existing.HasValue && existing.Value > DateTime.UtcNow)
        {
            newExpiration = existing.Value.Add(duration.Value);
            Console.WriteLine($"[Timer] {guildUser.Username}#{guildUser.IdentifyNumber} 的角色 \"{roleName}\" 已延期至 {newExpiration:yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            newExpiration = DateTime.UtcNow.Add(duration.Value);
            Console.WriteLine($"[Timer] {guildUser.Username}#{guildUser.IdentifyNumber} 的角色 \"{roleName}\" 将于 {newExpiration:yyyy-MM-dd HH:mm:ss} 过期");
        }

        await db.SetExpirationAsync(guild.Id, guildUser.Id, role.Id, newExpiration);
        var action = alreadyHasRole ? "已延期" : "已授予";
        replies.Add($"✅ {guildUser.Username}#{guildUser.IdentifyNumber} → {roleName} {action}，到期时间：{newExpiration:yyyy-MM-dd HH:mm:ss}");
    }

    if (replies.Count > 0)
    {
        var replyText = string.Join("\n", replies);
        Console.WriteLine($"[Reply] {replyText}");
        await channel.SendTextAsync(replyText, quote: new MessageReference(message.Id));
    }
    }
    catch (KookHttpException ex)
    {
        var msg = ex.HttpCode switch
        {
            HttpStatusCode.Forbidden => "Bot 缺少权限。请在服务器设置中：1) 给 Bot 角色开启「角色管理」权限；2) 确保 Bot 角色排在被管理的角色之上。",
            HttpStatusCode.NotFound => "目标用户或角色不存在。请检查角色名称是否正确。",
            HttpStatusCode.BadRequest => "请求参数错误。请检查命令格式是否正确。",
            HttpStatusCode.TooManyRequests => "操作过于频繁，请稍后再试。",
            _ => $"服务器返回错误 ({(int)ex.HttpCode}): {ex.Reason}"
        };
        Console.WriteLine($"[Error] {msg}");
        try { await channel.SendTextAsync(msg, quote: new MessageReference(message.Id)); } catch { }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Error] HandleMessage: {ex}");
    }
}

static TimeSpan? ParseDuration(string input)
{
    var match = Regex.Match(input, @"\+?(\d+)d", RegexOptions.IgnoreCase);
    if (match.Success && int.TryParse(match.Groups[1].Value, out var days) && days > 0)
        return TimeSpan.FromDays(days);
    return null;
}

static async Task CheckExpiredRolesAsync(KookSocketClient client, BotDatabase db)
{
    try
    {
        var expired = await db.GetExpiredRolesAsync();
        foreach (var (guildId, userId, roleId) in expired)
        {
            var guild = client.GetGuild(guildId);
            if (guild == null) continue;

            var user = guild.GetUser(userId);
            if (user == null)
            {
                await db.RemoveExpirationAsync(guildId, userId, roleId);
                continue;
            }

            var role = guild.GetRole(roleId);
            if (role != null && user.Roles.Any(r => r.Id == roleId))
            {
                try
                {
                    await user.RemoveRoleAsync(role);
                    Console.WriteLine($"[Expired] 已移除 {user.Username}#{user.IdentifyNumber} 的过期角色 \"{role.Name}\"");
                }
                catch (KookHttpException ex)
                {
                    Console.WriteLine($"[Error] 移除角色 {role.Name} 失败 ({(int)ex.HttpCode}): {ex.Reason}。请检查 Bot 是否拥有「角色管理」权限且角色层级足够高。");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to remove role {role.Name} from {user.Username}: {ex.Message}");
                }
            }

            await db.RemoveExpirationAsync(guildId, userId, roleId);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error checking expired roles: {ex.Message}");
    }
}

record BotConfig(string Token, string? DatabasePath, string? AdminRoleName);
