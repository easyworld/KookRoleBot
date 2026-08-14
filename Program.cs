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
    Log(log.Severity.ToString(), $"{log.Source}: {log.Message ?? log.Exception?.ToString()}");
    return Task.CompletedTask;
};

client.MessageReceived += (message, guildUser, textChannel) => HandleMessageAsync(client, db, config, message, guildUser, textChannel);

await client.LoginAsync(TokenType.Bot, config.Token);
await client.StartAsync();

Log("Startup", $"Bot started as {client.CurrentUser?.Username ?? "unknown"} (ID:{client.CurrentUser?.Id.ToString() ?? "unknown"}). Press Ctrl+C to exit.");

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

    Log("Message", $"收到消息 ID:{message.Id}, Guild:{channel.Guild?.Id.ToString() ?? "unknown"}, Channel:{channel.Id}, " +
        $"Sender:{sender.Username}#{sender.IdentifyNumber} (ID:{sender.Id}), Content:{ToLogValue(message.Content)}, " +
        $"MentionedUserIds:[{string.Join(",", message.MentionedUserIds)}]");

    var guild = channel.Guild;
    if (guild == null)
    {
        Log("Ignored", $"消息 ID:{message.Id} 无法取得服务器信息");
        return;
    }

    var adminRoleName = config.AdminRoleName ?? "管理员";
    if (!sender.Roles.Any(r => r.Name == adminRoleName))
    {
        Log("Ignored", $"消息 ID:{message.Id} 的发送者没有管理员角色 {ToLogValue(adminRoleName)}；" +
            $"发送者缓存角色:[{string.Join(", ", sender.Roles.Select(r => $"{r.Name}(ID:{r.Id})"))}]");
        return;
    }

    var currentUserId = client.CurrentUser?.Id ?? 0;
    if (currentUserId == 0)
    {
        Log("ParseRejected", $"消息 ID:{message.Id} 到达时 Bot 用户信息尚未就绪");
        return;
    }
    if (!message.MentionedUserIds.Contains(currentUserId))
    {
        Log("Ignored", $"消息 ID:{message.Id} 没有 @Bot (Bot ID:{currentUserId})");
        return;
    }

    var targetUserIds = message.MentionedUserIds
        .Where(id => id != currentUserId)
        .ToList();

    if (targetUserIds.Count == 0)
    {
        Log("ParseRejected", $"消息 ID:{message.Id} 没有目标用户；MentionedUserIds:[{string.Join(",", message.MentionedUserIds)}]");
        await channel.SendTextAsync("请 @mention 需要添加角色的用户。格式：@机器人 @用户 角色名 +时长", quote: new MessageReference(message.Id));
        return;
    }

    var content = Regex.Replace(message.Content, @"\(met\)[^()]*\(met\)|\(rol\)[^()]*\(rol\)|\(chn\)[^()]*\(chn\)", "").Trim();
    Log("Parse", $"消息 ID:{message.Id} 移除 mention 后的正文:{ToLogValue(content)}，目标用户:[{string.Join(",", targetUserIds)}]");

    var parts = content.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length < 2)
    {
        Log("ParseRejected", $"消息 ID:{message.Id} 正文字段不足；解析字段:[{string.Join(", ", parts.Select(ToLogValue))}]");
        await channel.SendTextAsync("格式错误。正确格式：@机器人 @用户 角色名 +时长（如 +1d）", quote: new MessageReference(message.Id));
        return;
    }

    var roleName = parts[0];
    var actionStr = string.Join(" ", parts[1..]);

    if (actionStr.Equals("-del", StringComparison.OrdinalIgnoreCase))
    {
        Log("Command", $"消息 ID:{message.Id} 解析为删除角色；Role:{ToLogValue(roleName)}, Targets:[{string.Join(",", targetUserIds)}]");
        await HandleDeleteRoleAsync(guild, db, targetUserIds, channel, message, roleName);
        return;
    }

    var duration = ParseDuration(actionStr);
    if (duration == null)
    {
        Log("ParseRejected", $"消息 ID:{message.Id} 时长无法解析；Role:{ToLogValue(roleName)}, Duration:{ToLogValue(actionStr)}");
        await channel.SendTextAsync("时长格式错误。请使用 +Nd（如 +1d, +7d），或使用 -del 删除角色", quote: new MessageReference(message.Id));
        return;
    }

    Log("Command", $"消息 ID:{message.Id} 解析为授予角色；Role:{ToLogValue(roleName)}, Duration:{duration.Value.TotalDays}d, " +
        $"Targets:[{string.Join(",", targetUserIds)}]");

    var role = guild.Roles.FirstOrDefault(r => r.Name == roleName);
    if (role == null)
    {
        Log("RoleRejected", $"消息 ID:{message.Id} 找不到角色 {ToLogValue(roleName)}；" +
            $"服务器缓存角色:[{string.Join(", ", guild.Roles.Select(r => $"{r.Name}(ID:{r.Id})"))}]");
        await channel.SendTextAsync($"服务器中不存在角色 \"{roleName}\"", quote: new MessageReference(message.Id));
        return;
    }

    Log("RoleState", $"准备通过 REST 刷新服务器用户状态；Message:{message.Id}, Guild:{guild.Id}");
    var restGuild = await client.Rest.GetGuildAsync(guild.Id);
    var replies = new List<string>();
    foreach (var userId in targetUserIds)
    {
        var guildUser = guild.GetUser(userId);
        if (guildUser == null)
        {
            Log("RoleRejected", $"消息 ID:{message.Id} 无法从服务器缓存取得目标用户 ID:{userId}");
            replies.Add($"用户 ID:{userId} 不在服务器中");
            continue;
        }

        var cacheHasRole = guildUser.Roles.Any(r => r.Id == role.Id);
        var refreshedUser = await restGuild.GetUserAsync(userId);
        if (refreshedUser == null)
        {
            Log("RoleRejected", $"消息 ID:{message.Id} 通过 REST 无法取得目标用户 ID:{userId}");
            replies.Add($"用户 ID:{userId} 不在服务器中");
            continue;
        }

        var serverHasRole = refreshedUser.RoleIds.Contains(role.Id);
        Log("RoleState", $"已刷新角色状态；Message:{message.Id}, Guild:{guild.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
            $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id}), CacheHasRole:{cacheHasRole}, ServerHasRole:{serverHasRole}");

        if (!serverHasRole)
        {
            Log("RoleGrant", $"准备调用 KOOK 授予接口；Message:{message.Id}, Guild:{guild.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
                $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id})");
            try
            {
                await refreshedUser.AddRoleAsync(role.Id);
                Log("RoleGrant", $"KOOK 授予接口成功；Message:{message.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
                    $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id})");
            }
            catch (Exception ex)
            {
                Log("RoleGrantFailed", $"KOOK 授予接口失败；Message:{message.Id}, Guild:{guild.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
                    $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id}), Error:{ex}");
                throw;
            }
        }

        var existing = await db.GetExpirationAsync(guild.Id, guildUser.Id, role.Id);
        DateTime newExpiration;
        if (existing.HasValue && existing.Value > DateTime.UtcNow)
        {
            newExpiration = existing.Value.Add(duration.Value);
            Log("Timer", $"{guildUser.Username}#{guildUser.IdentifyNumber} 的角色 \"{roleName}\" 已延期至 {newExpiration.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        }
        else
        {
            newExpiration = DateTime.UtcNow.Add(duration.Value);
            Log("Timer", $"{guildUser.Username}#{guildUser.IdentifyNumber} 的角色 \"{roleName}\" 将于 {newExpiration.ToLocalTime():yyyy-MM-dd HH:mm:ss} 过期");
        }

        await db.SetExpirationAsync(guild.Id, guildUser.Id, role.Id, newExpiration);
        var action = existing.HasValue && existing.Value > DateTime.UtcNow ? "已确认角色并延期" : "已授予";
        replies.Add($"✅ {guildUser.Username}#{guildUser.IdentifyNumber} → {roleName} {action}，到期时间：{newExpiration.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
    }

    if (replies.Count > 0)
    {
        var replyText = string.Join("\n", replies);
        Log("Reply", replyText);
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
            HttpStatusCode.OK => $"Bot 缺少权限：{ex.Reason}。请在服务器设置中：1) 给 Bot 角色开启「角色管理」权限；2) 确保 Bot 角色排在被管理的角色之上。",
            _ => $"服务器返回错误 ({(int)ex.HttpCode}): {ex.Reason}"
        };
        Log("HttpError", $"Message:{message.Id}, Status:{(int)ex.HttpCode} ({ex.HttpCode}), Reason:{ex.Reason}, Exception:{ex}");
        try
        {
            await channel.SendTextAsync(msg, quote: new MessageReference(message.Id));
        }
        catch (Exception replyException)
        {
            Log("ReplyFailed", $"发送错误提示失败；Message:{message.Id}, Error:{replyException}");
        }
    }
    catch (Exception ex)
    {
        Log("UnhandledError", $"处理消息失败；Message:{message.Id}, Guild:{channel.Guild?.Id.ToString() ?? "unknown"}, " +
            $"Channel:{channel.Id}, Sender:{sender.Id}, Content:{ToLogValue(message.Content)}, Error:{ex}");
    }
}

static async Task HandleDeleteRoleAsync(SocketGuild guild, BotDatabase db, List<ulong> targetUserIds, SocketTextChannel channel, SocketMessage message, string roleName)
{
    var role = guild.Roles.FirstOrDefault(r => r.Name == roleName);
    if (role == null)
    {
        Log("RoleRejected", $"消息 ID:{message.Id} 找不到待删除角色 {ToLogValue(roleName)}");
        await channel.SendTextAsync($"服务器中不存在角色 \"{roleName}\"", quote: new MessageReference(message.Id));
        return;
    }

    var replies = new List<string>();
    foreach (var userId in targetUserIds)
    {
        var guildUser = guild.GetUser(userId);
        if (guildUser == null)
        {
            Log("RoleRejected", $"消息 ID:{message.Id} 无法从服务器缓存取得待操作用户 ID:{userId}");
            replies.Add($"用户 ID:{userId} 不在服务器中");
            continue;
        }

        var hasRole = guildUser.Roles.Any(r => r.Id == role.Id);
        if (hasRole)
        {
            Log("RoleRemove", $"准备移除角色；Message:{message.Id}, Guild:{guild.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
                $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id})");
            await guildUser.RemoveRoleAsync(role);
            Log("RoleRemove", $"KOOK 移除接口成功；Message:{message.Id}, User:{guildUser.Username}#{guildUser.IdentifyNumber} " +
                $"(ID:{guildUser.Id}), Role:{role.Name} (ID:{role.Id})");
        }

        await db.RemoveExpirationAsync(guild.Id, guildUser.Id, role.Id);

        if (hasRole)
        {
            replies.Add($"❌ {guildUser.Username}#{guildUser.IdentifyNumber} → {roleName} 已移除");
        }
        else
        {
            replies.Add($"{guildUser.Username}#{guildUser.IdentifyNumber} 没有 {roleName} 角色，无需移除");
        }
    }

    if (replies.Count > 0)
    {
        var replyText = string.Join("\n", replies);
        Log("Reply", replyText);
        await channel.SendTextAsync(replyText, quote: new MessageReference(message.Id));
    }
}

static TimeSpan? ParseDuration(string input)
{
    var match = Regex.Match(input, @"^\+?(\d+)d$", RegexOptions.IgnoreCase);
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
                    Log("Expired", $"已移除 {user.Username}#{user.IdentifyNumber} 的过期角色 \"{role.Name}\"");
                }
                catch (KookHttpException ex)
                {
                    Log("RoleRemoveFailed", $"移除过期角色失败；Guild:{guildId}, User:{userId}, Role:{role.Name} (ID:{roleId}), " +
                        $"Status:{(int)ex.HttpCode} ({ex.HttpCode}), Reason:{ex.Reason}, Error:{ex}");
                }
                catch (Exception ex)
                {
                    Log("RoleRemoveFailed", $"移除过期角色失败；Guild:{guildId}, User:{userId}, Role:{role.Name} (ID:{roleId}), Error:{ex}");
                }
            }

            await db.RemoveExpirationAsync(guildId, userId, roleId);
        }
    }
    catch (Exception ex)
    {
        Log("ExpirationError", $"检查过期角色失败：{ex}");
    }
}

static void Log(string category, string message) =>
    Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{category}] {message}");

static string ToLogValue(string? value) => JsonSerializer.Serialize(value);

record BotConfig(string Token, string? DatabasePath, string? AdminRoleName);
