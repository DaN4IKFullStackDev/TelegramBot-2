using System;
using System.IO; // System.IO.File
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Collections.Concurrent;

using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types; // Contains Telegram.Bot.Types.File
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Data.Sqlite;

// --- НАСТРОЙКИ ---
var GameSessions = new System.Collections.Concurrent.ConcurrentDictionary<long, (int Remaining, int PerTap, DateTime Expires)>();
var UserTapBonus = new System.Collections.Concurrent.ConcurrentDictionary<long, int>();
var botToken = ("8933392118:AAF8afXjq3ZXOEHTRI2Go7lBmb-DqsLOuaQ"); // ВСТАВЬ СВОЙ ТОКЕН
var botClient = new TelegramBotClient(botToken);

const string DbFileName = "users.db";
const string ConnectionString = $"Data Source={DbFileName}";

// Идентификатор администратора
const long AdminChatId = 7817912892;

// Инициализация БД
InitDb(); 

using var cts = new CancellationTokenSource();

// Настройки получения сообщений
var receiverOptions = new ReceiverOptions
{
    AllowedUpdates = Array.Empty<UpdateType>() 
};

Console.WriteLine("🚀 Запуск бота...");

botClient.StartReceiving(
    updateHandler: HandleUpdateAsync,
    HandlePollingErrorAsync, 
    receiverOptions: receiverOptions,
    cancellationToken: cts.Token
);

var me = await botClient.GetMeAsync();
Console.WriteLine($"✅ Бот @{me.Username} запущен! Нажми Enter для выключения.");
Console.ReadLine();
cts.Cancel();

// --- ОБРАБОТКА ОБНОВЛЕНИЙ ---
async Task HandleUpdateAsync(ITelegramBotClient botClient, Update update, CancellationToken cancellationToken)
{
    long chatId = 0;
    string text;
    string username;
    string firstName;
    int pointsEarned = 0; // Очки, заработанные за это действие

    // 1. Если нажата КНОПКА
    if (update.Type == UpdateType.CallbackQuery && update.CallbackQuery != null)
    {
        chatId = update.CallbackQuery.Message!.Chat.Id;
        string data = update.CallbackQuery.Data ?? "";
        
        var (response, callbackKeyboard) = GetCallbackResponse(data, chatId);
        
        pointsEarned = 0; // Очки начисляются внутри обработчиков (если нужно)

        await botClient.AnswerCallbackQueryAsync(update.CallbackQuery.Id, cancellationToken: cancellationToken);
        await botClient.SendTextMessageAsync(chatId, response, replyMarkup: callbackKeyboard, cancellationToken: cancellationToken);
    }
    // 2. Если пришло СООБЩЕНИЕ
    else if (update.Type == UpdateType.Message && update.Message?.Text != null)
    {
        var message = update.Message;
        chatId = message.Chat.Id;
        text = message.Text.ToLower().Trim();
        username = message.Chat.Username ?? "Аноним";
        firstName = message.Chat.FirstName ?? "бро";

        RegisterUserInDb(chatId, username, firstName);

        if (chatId != AdminChatId && IsUserBanned(chatId))
        {
            await botClient.SendTextMessageAsync(chatId, "❌ Вы забанены и не можете использовать бота.", cancellationToken: cancellationToken);
            return;
        }

        string responseText;
        InlineKeyboardMarkup? keyboard = null;

        if (text == "/start" || text == "меню")
        {
            keyboard = MainMenuKeyboard(chatId);
            responseText = GetMainMenuText(firstName);
            pointsEarned = 0;
        }
        else if (text.StartsWith("/meadd ") || text.StartsWith("добавь себе "))
        {
            if (!IsAdmin(chatId))
            {
                responseText = "❌ Только админ может использовать эту команду.";
            }
            else
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || !int.TryParse(parts[1], out var amount))
                    responseText = "Использование: /meadd <amount>";
                else
                {
                    EnsureUserExists(chatId);
                    AdjustPoints(chatId, amount);
                    responseText = $"✅ Тебе добавлено {amount} очков.";
                }
            }
            pointsEarned = 0;
        }
        else if (text.StartsWith("/mesetpoints ") || text.StartsWith("/meset "))
        {
            if (!IsAdmin(chatId))
            {
                responseText = "❌ Только админ может использовать эту команду.";
            }
            else
            {
                var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !int.TryParse(parts[1], out var amount))
                    responseText = "Использование: /mesetpoints <amount>";
                else
                {
                    EnsureUserExists(chatId);
                    SetUserPoints(chatId, amount);
                    responseText = $"✅ Твои очки установлены в {amount}.";
                }
            }
            pointsEarned = 0;
        }
        else if (text.StartsWith("накрутить очков ") || text.StartsWith("/addpoints "))
        {
            responseText = HandleAdminAddPoints(text, chatId);
            pointsEarned = 0;
        }
        else if (text.StartsWith("/setpoints "))
        {
            responseText = HandleAdminSetPoints(text, chatId);
            pointsEarned = 0;
        }
        else if (text.StartsWith("/removeuser "))
        {
            responseText = HandleAdminRemoveUser(text, chatId);
            pointsEarned = 0;
        }
        else if (text.StartsWith("/ban ") || text.StartsWith("/unban "))
        {
            responseText = HandleAdminBanCommand(text, chatId);
            pointsEarned = 0;
        }
        else if (text == "/admin" || (chatId == AdminChatId && text == AdminChatId.ToString()))
        {
            if (IsAdmin(chatId))
            {
                responseText = GetAdminMenu();
            }
            else
            {
                responseText = "❌ Только админ может открыть это меню.";
            }
            pointsEarned = 0;
        }
        else if (text == "/top") 
        {
            responseText = GetLeaderboard();
            pointsEarned = 3; // Очки за просмотр топа
        }
        else if (text == "/startgame")
        {
            var target = Random.Shared.Next(8, 16);
            var perTap = 2 + GetUserTapBonus(chatId) + (GetUserRole(chatId) == "legend" ? 1 : 0) + GetLegendActiveBoostBonus(chatId);
            GameSessions[chatId] = (Remaining: target, PerTap: perTap, Expires: DateTime.UtcNow.AddMinutes(1));
            responseText = $"🐹 Игра началась! Набери {target} тапов за 1 минуту. Каждый тап дает {perTap} очков.";
            keyboard = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData("Тап", "game_tap") });
            pointsEarned = 0;
            ResetUserTapBonus(chatId);
        }
        else if (text == "/tap" || text == "тап")
        {
            if (GameSessions.TryGetValue(chatId, out var gs) && gs.Expires > DateTime.UtcNow && gs.Remaining > 0)
            {
                var (rem, per, exp) = gs;
                var newGs = (Remaining: rem - 1, PerTap: per, Expires: exp);
                GameSessions.TryUpdate(chatId, newGs, gs);
                AddPoints(chatId, per);
                if (newGs.Remaining <= 0)
                {
                    GameSessions.TryRemove(chatId, out _);
                    responseText = $"🎉 Поздравляем — вы сделали все тап(ы)! Награда начислена.";
                }
                else
                {
                    responseText = $"Тап! Осталось {newGs.Remaining} тапов. (+{per} очков)";
                    keyboard = new InlineKeyboardMarkup(new[] { InlineKeyboardButton.WithCallbackData("Тап", "game_tap") });
                }
            }
            else
            {
                responseText = "Нет активной игры. Начни /startgame чтобы начать.";
            }
            pointsEarned = 0;
        }
        else if (text == "/listusers")
        {
            if (!IsAdmin(chatId))
                responseText = "❌ Только админ может использовать эту команду.";
            else
                responseText = GetUsersList();
            pointsEarned = 0;
        }
        else if (text == "/id")
        {
            responseText = $"Твой chatId: {chatId}";
            pointsEarned = 0;
        }
        else if (text == "привет")
        {
            responseText = $"Привет, {firstName}! 👋 Чем помочь?";
            pointsEarned = 1;
        }
        else if (text == "профиль") 
        {
             int points = GetUserPoints(chatId);
             responseText = $"👤 Профиль: {firstName}\n💰 Твои очки: {points}";
             pointsEarned = 1;
        }
        else
        {
            keyboard = MainMenuKeyboard(chatId);
            responseText = GetMainMenuText(firstName);
            pointsEarned = 0;
        }

        await botClient.SendTextMessageAsync(chatId, responseText, replyMarkup: keyboard, cancellationToken: cancellationToken);
    }
    
    // Начисляем очки, если они были заработаны
    if (pointsEarned > 0)
    {
        AddPoints(chatId, pointsEarned);
    }
}

// --- ОБРАБОТКА ОШИБОК ---
Task HandlePollingErrorAsync(ITelegramBotClient botClient, Exception exception, CancellationToken cancellationToken)
{
    Console.WriteLine($"❌ Ошибка API: {exception.Message}");
    return Task.CompletedTask;
}

// --- БАЗА ДАННЫХ (SQL) ---
void InitDb()
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        CREATE TABLE IF NOT EXISTS Users (
            ChatId INTEGER PRIMARY KEY, 
            Username TEXT, 
            FirstName TEXT, 
            Points INTEGER DEFAULT 0,
            IsBanned INTEGER DEFAULT 0,
            Role TEXT DEFAULT 'player',
            VipAdminExpires TEXT
        );"; 
    cmd.ExecuteNonQuery();

    // Проверяем, какие колонки действительно есть в таблице (на случай старой БД)
    cmd.CommandText = "PRAGMA table_info(Users);";
    using var reader = cmd.ExecuteReader();
    bool hasIsBanned = false;
    bool hasRole = false;
    bool hasVip = false;
    while (reader.Read())
    {
        var col = reader.GetString(1);
        if (col == "IsBanned") hasIsBanned = true;
        if (col == "Role") hasRole = true;
        if (col == "VipAdminExpires") hasVip = true;
    }
    reader.Close();

    if (!hasIsBanned)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN IsBanned INTEGER DEFAULT 0;";
        cmd.ExecuteNonQuery();
    }
    if (!hasRole)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN Role TEXT DEFAULT 'player';";
        cmd.ExecuteNonQuery();
    }
    if (!hasVip)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN VipAdminExpires TEXT;";
        cmd.ExecuteNonQuery();
    }
    // Доп. поля для фишки Легенды
    if (!reader.IsClosed) reader.Close();
    cmd.CommandText = "PRAGMA table_info(Users);";
    using var reader2 = cmd.ExecuteReader();
    bool hasLegendExpires = false;
    bool hasLegendLastUsed = false;
    while (reader2.Read())
    {
        var col = reader2.GetString(1);
        if (col == "LegendBoostExpires") hasLegendExpires = true;
        if (col == "LegendBoostLastUsed") hasLegendLastUsed = true;
    }
    reader2.Close();
    if (!hasLegendExpires)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN LegendBoostExpires TEXT;";
        cmd.ExecuteNonQuery();
    }
    if (!hasLegendLastUsed)
    {
        cmd.CommandText = "ALTER TABLE Users ADD COLUMN LegendBoostLastUsed TEXT;";
        cmd.ExecuteNonQuery();
    }

    Console.WriteLine("✅ БД готова.");
}

void RegisterUserInDb(long chatId, string username, string firstName)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = @"
        INSERT INTO Users (ChatId, Username, FirstName, Points, IsBanned, Role, VipAdminExpires)
        VALUES ($id, $u, $f, COALESCE((SELECT Points FROM Users WHERE ChatId = $id), 0), COALESCE((SELECT IsBanned FROM Users WHERE ChatId = $id), 0), COALESCE((SELECT Role FROM Users WHERE ChatId = $id), 'player'), COALESCE((SELECT VipAdminExpires FROM Users WHERE ChatId = $id), NULL))
        ON CONFLICT(ChatId) DO UPDATE SET Username = $u, FirstName = $f;";
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.Parameters.AddWithValue("$u", username);
    cmd.Parameters.AddWithValue("$f", firstName);
    cmd.ExecuteNonQuery();
}

// Функция для начисления очков
void AddPoints(long chatId, int amount)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET Points = Points + $amount WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$amount", amount);
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}

// Функция получения очков пользователя
int GetUserPoints(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT Points FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    
    var result = cmd.ExecuteScalar();
    
    if (result == null || result == DBNull.Value)
        return 0;
        
    return Convert.ToInt32(result);
}

// Функция получения таблицы лидеров
string GetLeaderboard()
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT FirstName, Points FROM Users ORDER BY Points DESC LIMIT 10"; 
    
    var reader = cmd.ExecuteReader();
    
    if (!reader.HasRows)
        return "🏆 Пока никто не набрал очки!";
        
    string leaderboard = "🏆 Топ игроков:\n";
    int place = 1;
    while(reader.Read())
    {
        leaderboard += $"{place}. {reader.GetString(0)} — {reader.GetInt32(1)} очков\n";
        place++;
    }
    return leaderboard;
}

bool IsAdmin(long chatId)
{
    return chatId == AdminChatId;
}

bool AdjustPoints(long chatId, int amount)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();

    var insertCmd = connection.CreateCommand();
    insertCmd.CommandText = "INSERT OR IGNORE INTO Users (ChatId, Username, FirstName, Points) VALUES ($id, '', '', 0);";
    insertCmd.Parameters.AddWithValue("$id", chatId);
    insertCmd.ExecuteNonQuery();

    var updateCmd = connection.CreateCommand();
    updateCmd.CommandText = "UPDATE Users SET Points = Points + $amount WHERE ChatId = $id";
    updateCmd.Parameters.AddWithValue("$amount", amount);
    updateCmd.Parameters.AddWithValue("$id", chatId);
    return updateCmd.ExecuteNonQuery() > 0;
}

string HandleAdminAddPoints(string text, long chatId)
{
    if (!IsAdmin(chatId))
        return "❌ Только админ может использовать эту команду.";

    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    int argIndex = parts[0] == "/addpoints" ? 1 : 2;

    if (parts.Length <= argIndex + 1)
        return "Использование: накрутить очков <chatId> <amount>";

    if (!long.TryParse(parts[argIndex], out var targetChatId))
        return "❌ Неверный chatId.";

    if (!int.TryParse(parts[argIndex + 1], out var amount))
        return "❌ Неверное количество очков.";

    EnsureUserExists(targetChatId);
    AdjustPoints(targetChatId, amount);
    return $"✅ Админ добавил {amount} очков пользователю {targetChatId}.";
}

string HandleAdminSetPoints(string text, long chatId)
{
    if (!IsAdmin(chatId))
        return "❌ Только админ может использовать эту команду.";

    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 3)
        return "Использование: /setpoints <chatId> <amount>";

    if (!long.TryParse(parts[1], out var targetChatId))
        return "❌ Неверный chatId.";

    if (!int.TryParse(parts[2], out var amount))
        return "❌ Неверное количество очков.";

    EnsureUserExists(targetChatId);
    return SetUserPoints(targetChatId, amount)
        ? $"✅ Очки пользователя {targetChatId} установлены в {amount}."
        : "❌ Не удалось обновить очки.";
}

string HandleAdminRemoveUser(string text, long chatId)
{
    if (!IsAdmin(chatId))
        return "❌ Только админ может использовать эту команду.";

    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2)
        return "Использование: /removeuser <chatId>";

    if (!long.TryParse(parts[1], out var targetChatId))
        return "❌ Неверный chatId.";

    return RemoveUser(targetChatId)
        ? $"✅ Пользователь {targetChatId} удалён из таблицы."
        : "❌ Пользователь не найден.";
}

string HandleAdminBanCommand(string text, long chatId)
{
    if (!IsAdmin(chatId))
        return "❌ Только админ может использовать эту команду.";

    var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length != 2)
        return "Использование: /ban <chatId> или /unban <chatId>";

    if (!long.TryParse(parts[1], out var targetChatId))
        return "❌ Неверный chatId.";

    bool ban = parts[0] == "/ban";
    EnsureUserExists(targetChatId);
    return SetUserBan(targetChatId, ban)
        ? (ban ? $"✅ Пользователь {targetChatId} забанен." : $"✅ Пользователь {targetChatId} разбанен.")
        : "❌ Не удалось обновить статус бана.";
}

void EnsureUserExists(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "INSERT OR IGNORE INTO Users (ChatId, Username, FirstName, Points, IsBanned, Role, VipAdminExpires) VALUES ($id, '', '', 0, 0, 'player', NULL);";
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}

bool SetUserPoints(long chatId, int points)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET Points = $points WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$points", points);
    cmd.Parameters.AddWithValue("$id", chatId);
    return cmd.ExecuteNonQuery() > 0;
}

bool RemoveUser(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "DELETE FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    return cmd.ExecuteNonQuery() > 0;
}

bool SetUserBan(long chatId, bool banned)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    EnsureUserExists(chatId);
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET IsBanned = $banned WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$banned", banned ? 1 : 0);
    cmd.Parameters.AddWithValue("$id", chatId);
    return cmd.ExecuteNonQuery() > 0;
}

bool IsUserBanned(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT IsBanned FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    var result = cmd.ExecuteScalar();
    return result != null && result != DBNull.Value && Convert.ToInt32(result) == 1;
}

string GetAdminMenu()
{
    return "🛠 Админ-меню:\n" +
           "/addpoints <chatId> <amount> — добавить очки\n" +
           "/setpoints <chatId> <amount> — установить очки\n" +
           "/removeuser <chatId> — удалить пользователя\n" +
           "/ban <chatId> — забанить пользователя\n" +
           "/unban <chatId> — разбанить пользователя\n" +
           "/top — показать таблицу лидеров\n" +
           "/id — показать свой chatId\n" +
           "/meadd <amount> — добавить себе любое количество очков\n" +
           "/mesetpoints <amount> — установить себе количество очков";
}

string GetUsersList(int limit = 50)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT ChatId, FirstName, Username, Points FROM Users ORDER BY Points DESC LIMIT $limit";
    cmd.Parameters.AddWithValue("$limit", limit);

    using var reader = cmd.ExecuteReader();
    if (!reader.HasRows) return "Пользователей не найдено.";

    var sb = new System.Text.StringBuilder();
    while (reader.Read())
    {
        var id = reader.GetInt64(0);
        var name = reader.IsDBNull(1) ? "-" : reader.GetString(1);
        var username = reader.IsDBNull(2) ? "-" : reader.GetString(2);
        var points = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
        sb.AppendLine($"{id} | {name} | @{username} | {points} очк.");
    }

    return sb.ToString();
}

(string text, InlineKeyboardMarkup? keyboard) GetCallbackResponse(string data, long chatId)
{
    if (data == "admin_menu")
    {
        return HasAdminAccess(chatId)
            ? (GetAdminMenu(), MainMenuKeyboard(chatId))
            : ("❌ Только админ или VIP могут использовать это меню.", MainMenuKeyboard(chatId));
    }

    if (data == "menu_game")
    {
        var target = Random.Shared.Next(8, 16);
        var perTap = 2 + GetUserTapBonus(chatId) + (GetUserRole(chatId) == "legend" ? 1 : 0) + GetLegendActiveBoostBonus(chatId);
        GameSessions[chatId] = (Remaining: target, PerTap: perTap, Expires: DateTime.UtcNow.AddMinutes(1));
        ResetUserTapBonus(chatId);
        return ($"🐹 Игра началась! Набери {target} тапов за 1 минуту. Каждый тап дает {perTap} очков.",
                new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Тап", "game_tap")));
    }

    if (data == "menu_top")
    {
        return (GetLeaderboard(), MainMenuKeyboard(chatId));
    }

    if (data == "menu_balance")
    {
        return ($"💰 Твои очки: {GetUserPoints(chatId)}", MainMenuKeyboard(chatId));
    }

    if (data == "menu_shop")
    {
        return (GetShopMenu(chatId), ShopKeyboard(chatId));
    }

    if (data == "shop_buy_bonus")
    {
        return (ProcessShopPurchase(chatId, 100, "Плюс +1 очко за тап", () =>
        {
            UserTapBonus.AddOrUpdate(chatId, 1, (_, current) => current + 1);
        }), ShopKeyboard(chatId));
    }

    if (data == "shop_buy_points")
    {
        return (ProcessShopPurchase(chatId, 50, "Мгновенно +10 очков", () =>
        {
            AddPoints(chatId, 10);
        }), ShopKeyboard(chatId));
    }

    if (data == "shop_buy_vip_admin")
    {
        return (ProcessShopPurchase(chatId, 10000, "админ-меню на 1 минуту", () =>
        {
            SetVipAdminExpiry(chatId, DateTime.UtcNow.AddMinutes(1));
        }), ShopKeyboard(chatId));
    }

    if (data == "shop_buy_legend")
    {
        return (ProcessShopPurchase(chatId, 5000, "роль Легенда", () =>
        {
            SetUserRole(chatId, "legend");
        }), ShopKeyboard(chatId));
    }

    if (data == "menu_legend")
    {
        return (GetLegendMenu(chatId), LegendKeyboard(chatId));
    }

    if (data == "legend_activate_boost")
    {
        if (GetUserRole(chatId) != "legend")
            return ("❌ Только Легенда может активировать эту фишку.", LegendKeyboard(chatId));

        if (!CanUseLegendBoost(chatId))
        {
            var last = GetLegendBoostLastUsed(chatId);
            var next = last.HasValue ? last.Value.AddHours(24) : DateTime.UtcNow;
            return ($"❌ Фишка в кулдауне. Доступна: {next.ToLocalTime():g}", LegendKeyboard(chatId));
        }

        // Активируем буст на 1 минуту
        SetLegendBoostExpiry(chatId, DateTime.UtcNow.AddMinutes(1));
        SetLegendBoostLastUsed(chatId, DateTime.UtcNow);
        return ("✨ Легендарный буст активирован на 1 минуту! Твои тап-очки временно увеличены.", LegendKeyboard(chatId));
    }

    if (data == "back_to_menu")
    {
        return (GetMainMenuText("бро"), MainMenuKeyboard(chatId));
    }

    if (data == "game_tap")
    {
        if (GameSessions.TryGetValue(chatId, out var gs) && gs.Expires > DateTime.UtcNow && gs.Remaining > 0)
        {
            var (rem, per, exp) = gs;
            var newGs = (Remaining: rem - 1, PerTap: per, Expires: exp);
            GameSessions.TryUpdate(chatId, newGs, gs);
            AddPoints(chatId, per);
            if (newGs.Remaining <= 0)
            {
                GameSessions.TryRemove(chatId, out _);
                return ("🎉 Поздравляем — вы сделали все тап(ы)! Награда начислена.", MainMenuKeyboard(chatId));
            }
            return ($"Тап! Осталось {newGs.Remaining} тапов. (+{per} очков)", new InlineKeyboardMarkup(InlineKeyboardButton.WithCallbackData("Тап", "game_tap")));
        }
        return ("Нет активной игры. Начни игру в меню.", MainMenuKeyboard(chatId));
    }

    return (GetMainMenuText("бро"), MainMenuKeyboard(chatId));
}

string GetMainMenuText(string firstName)
{
    return $"Привет, {firstName}! Я твой игровой помощник. Выбирай действие ниже:";
}

InlineKeyboardMarkup MainMenuKeyboard(long chatId)
{
    var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("🎮 Игра", "menu_game"), InlineKeyboardButton.WithCallbackData("🏆 Топ", "menu_top") },
        new[] { InlineKeyboardButton.WithCallbackData("💰 Баланс", "menu_balance"), InlineKeyboardButton.WithCallbackData("🛒 Магазин", "menu_shop") }
    };

    if (HasAdminAccess(chatId))
    {
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("🛠 Админ меню", "admin_menu") });
    }

    if (GetUserRole(chatId) == "legend")
    {
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("♛ Легенда", "menu_legend") });
    }

    return new InlineKeyboardMarkup(buttons.ToArray());
}

InlineKeyboardMarkup ShopKeyboard(long chatId)
{
    var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("+1 очко за тап (100)", "shop_buy_bonus") },
        new[] { InlineKeyboardButton.WithCallbackData("+10 очков сразу (50)", "shop_buy_points") },
        new[] { InlineKeyboardButton.WithCallbackData("Админ-меню на 1 минуту (10000)", "shop_buy_vip_admin") }
    };

    if (GetUserRole(chatId) != "legend")
    {
        buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Роль Легенда (5000)", "shop_buy_legend") });
    }

    buttons.Add(new[] { InlineKeyboardButton.WithCallbackData("Назад", "back_to_menu") });

    return new InlineKeyboardMarkup(buttons.ToArray());
}

string GetShopMenu(long chatId)
{
    var role = GetUserRole(chatId);
    var roleText = role == "legend" ? "Ты уже Легенда — у тебя есть особые фишки!" : "Купи роль Легенда, чтобы открыть специальное меню.";

    return "🛒 Магазин бонусов:\n" +
           "- +1 очко за тап (100 очков)\n" +
           "- +10 очков сразу (50 очков)\n" +
           "- Админ-меню на 1 минуту (10000 очков)\n" +
           "- Роль Легенда (5000 очков)\n\n" +
           $"Твой баланс: {GetUserPoints(chatId)} очков.\n" +
           roleText;
}

string ProcessShopPurchase(long chatId, int cost, string itemName, Action onPurchase)
{
    var balance = GetUserPoints(chatId);
    if (balance < cost)
        return $"❌ Недостаточно очков для покупки {itemName}. Нужно {cost} очков.";

    AdjustPoints(chatId, -cost);
    onPurchase();
    return $"✅ Вы купили {itemName}!";
}

int GetUserTapBonus(long chatId)
{
    return UserTapBonus.TryGetValue(chatId, out var bonus) ? bonus : 0;
}

void ResetUserTapBonus(long chatId)
{
    UserTapBonus.TryRemove(chatId, out _);
}

bool HasAdminAccess(long chatId)
{
    return IsAdmin(chatId) || IsVipAdmin(chatId);
}

bool IsVipAdmin(long chatId)
{
    var expiry = GetVipAdminExpiry(chatId);
    return expiry.HasValue && expiry.Value > DateTime.UtcNow;
}

DateTime? GetVipAdminExpiry(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT VipAdminExpires FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    var result = cmd.ExecuteScalar();
    if (result == null || result == DBNull.Value)
        return null;
    return DateTime.TryParse(result.ToString(), out var parsed) ? parsed : null;
}

void SetVipAdminExpiry(long chatId, DateTime? expiry)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    EnsureUserExists(chatId);
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET VipAdminExpires = $expiry WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$expiry", expiry?.ToString("o") ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}

string GetUserRole(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT Role FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    var result = cmd.ExecuteScalar();
    return result == null || result == DBNull.Value ? "player" : result.ToString() ?? "player";
}

void SetUserRole(long chatId, string role)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    EnsureUserExists(chatId);
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET Role = $role WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$role", role);
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}

string GetLegendMenu(long chatId)
{
    return "♛ Меню Легенды:\n" +
           "- Ты получаешь +1 очко за каждый тап\n" +
           "- Можно открыть особую легендарную фишку\n" +
           "- Админ и VIP остаются доступными, если ты админ\n\n" +
           $"Твоя роль: {GetUserRole(chatId)}\n" +
           "Нажми на `Назад`, чтобы вернуться.";
}

InlineKeyboardMarkup LegendKeyboard(long chatId)
{
    var buttons = new List<InlineKeyboardButton[]>
    {
        new[] { InlineKeyboardButton.WithCallbackData("Назад", "back_to_menu") }
    };

    if (GetUserRole(chatId) == "legend")
    {
        buttons.Insert(0, new[] { InlineKeyboardButton.WithCallbackData("✨ Активировать буст (1 мин)", "legend_activate_boost") });
    }

    return new InlineKeyboardMarkup(buttons.ToArray());
}

// Класс и состояние для мини-игры (должны быть после top-level инструкций)
// (GameSession class removed; using tuple-based GameSessions)

int GetLegendActiveBoostBonus(long chatId)
{
    var expiry = GetLegendBoostExpiry(chatId);
    return expiry.HasValue && expiry.Value > DateTime.UtcNow ? 1 : 0;
}

DateTime? GetLegendBoostExpiry(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT LegendBoostExpires FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    var result = cmd.ExecuteScalar();
    if (result == null || result == DBNull.Value) return null;
    return DateTime.TryParse(result.ToString(), out var parsed) ? parsed : null;
}

DateTime? GetLegendBoostLastUsed(long chatId)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    var cmd = connection.CreateCommand();
    cmd.CommandText = "SELECT LegendBoostLastUsed FROM Users WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$id", chatId);
    var result = cmd.ExecuteScalar();
    if (result == null || result == DBNull.Value) return null;
    return DateTime.TryParse(result.ToString(), out var parsed) ? parsed : null;
}

bool CanUseLegendBoost(long chatId)
{
    var last = GetLegendBoostLastUsed(chatId);
    if (!last.HasValue) return true;
    return last.Value.AddHours(24) <= DateTime.UtcNow;
}

void SetLegendBoostExpiry(long chatId, DateTime? expiry)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    EnsureUserExists(chatId);
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET LegendBoostExpires = $expiry WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$expiry", expiry?.ToString("o") ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}

void SetLegendBoostLastUsed(long chatId, DateTime? time)
{
    using var connection = new SqliteConnection(ConnectionString);
    connection.Open();
    EnsureUserExists(chatId);
    var cmd = connection.CreateCommand();
    cmd.CommandText = "UPDATE Users SET LegendBoostLastUsed = $t WHERE ChatId = $id";
    cmd.Parameters.AddWithValue("$t", time?.ToString("o") ?? (object)DBNull.Value);
    cmd.Parameters.AddWithValue("$id", chatId);
    cmd.ExecuteNonQuery();
}
