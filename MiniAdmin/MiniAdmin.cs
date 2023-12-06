using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Entities;
using CounterStrikeSharp.API.Modules.Menu;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
// using CounterStrikeSharp.API.Modules.Commands.Targeting;
using CSSTargetResult = CounterStrikeSharp.API.Modules.Commands.Targeting.TargetResult;
using Dapper;
using MySqlConnector;

namespace MiniAdmin;

public class MiniAdmin : BasePlugin
{
    public override string ModuleAuthor => "Nubezzi";
    public override string ModuleName => "MiniAdmin-Nubezzi";
    public override string ModuleVersion => "v0.0.1";

    private string _dbConnectionString = string.Empty;

    private DateTime[] _playerPlayTime = new DateTime[Server.MaxPlayers];

    private static readonly string Prefix = "[\x0C Admin Menu \x01]";
    private readonly ChatMenu _slayMenu = new(Prefix + " Slay");

    public override void Load(bool hotReload)
    {
        _dbConnectionString = BuildConnectionString();
        CreateTable(_dbConnectionString);
        CreateAdminsTable(_dbConnectionString);

        var path = Path.Combine(ModuleDirectory, "maps.txt");
        if(!File.Exists(path))
            File.WriteAllLines(path, new []{ "de_dust2" });

        RegisterListener<Listeners.OnClientConnected>(slot =>
        {
            var entity = NativeAPI.GetEntityFromIndex(slot + 1);

            if (entity == IntPtr.Zero) return;

            var player = new CCSPlayerController(entity);

            OnClientConnectedAsync(slot, player, new SteamID(player.SteamID));
        });

        RegisterListener<Listeners.OnClientDisconnectPost>(slot => { _playerPlayTime[slot + 1] = DateTime.MinValue; });

        AddTimer(300, () =>
        {
           Timer_DeleteAdminAsync();
        }, TimerFlags.REPEAT);
        CreateMenu();
    }

    private async Task Timer_DeleteAdminAsync()
    {
        await using var connection = new MySqlConnection(_dbConnectionString);
            
        var deleteAdmins = await connection.QueryAsync<Admins>(
            "SELECT * FROM miniadmin_admins WHERE EndTime <= @CurrentTime AND EndTime > 0",
            new { CurrentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

        var adminsEnumerable = deleteAdmins.ToList();
        if (adminsEnumerable.Any())
        {
            foreach (var result in adminsEnumerable.Select(deleteAdmin => DeleteAdmin(deleteAdmin.SteamId)))
                PrintToServer(await result, ConsoleColor.DarkMagenta);
        }
    }

    private async Task OnClientConnectedAsync(int slot, CCSPlayerController player, SteamID steamId)
    {
        try
        {
            using var connection = new MySqlConnection(_dbConnectionString);
            var unbanUsers = connection.Query<User>(
                "SELECT * FROM miniadmin_bans WHERE EndBanTime <= @CurrentTime AND BanActive = 1 AND EndBanTime > 0",
                new { CurrentTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });
            foreach (var user in unbanUsers)
            {
                PrintToServer($"Unban: {user.SteamId}", ConsoleColor.DarkMagenta);
                UnbanUser("Console", "Console", user.SteamId, "The deadline has passed");
            }
            Timer_DeleteAdminAsync();
            var banUser = connection.QueryFirstOrDefault<User>(
                "SELECT * FROM miniadmin_bans WHERE SteamId64 = @SteamId64 AND BanActive = 1",
                new { steamId.SteamId64 });
            if (banUser != null) Server.ExecuteCommand($"kick {player.PlayerName}");
            else
                _playerPlayTime[slot + 1] = DateTime.Now;
        }
        catch (Exception e)
        {
            var textsthi = Convert.ToString(e);
            Server.PrintToConsole(textsthi);
        }
    }

    private void CreateMenu()
    {
        var adminMenu = new ChatMenu(Prefix);
        adminMenu.AddMenuOption("Slay players", (player, option) =>
        {
            if (!IsAdmin(player))
            {
                PrintToChat(player, "you do not have access to this command");
                return;
            }

            CreateSlayMenu();
            ChatMenus.OpenMenu(player, _slayMenu);
        });

        AddCommand("css_admin", "admin menu", (player, info) =>
        {
            if (player == null) return;
            if (!IsAdmin(player))
            {
                PrintToChat(player, "you do not have access to this command");
                return;
            }

            ChatMenus.OpenMenu(player, adminMenu);
        });
    }

    private void CreateSlayMenu()
    {
        var playerEntities = Utilities.GetPlayers();
        _slayMenu.MenuOptions.Clear();
        _slayMenu.AddMenuOption("All", (controller, option) =>
        {
            foreach (var player in playerEntities) player.PlayerPawn.Value.CommitSuicide(true, true);
        });
        foreach (var player in playerEntities)
        {
            if(!player.PawnIsAlive) continue;
            
            _slayMenu.AddMenuOption($"{player.PlayerName} [{player.Index!}]", (controller, option) =>
            {
                var parts = option.Text.Split('[', ']');
                if (parts.Length < 2) return;
                var target = Utilities.GetPlayerFromIndex(int.Parse(parts[1]));
                    
                if (!target.PawnIsAlive)
                {
                    PrintToChat(controller, "The player is already dead");
                    return;
                }

                target.PlayerPawn.Value.CommitSuicide(true, true);

                PrintToChatAll($"{controller.PlayerName}: Player '{target.PlayerName}' has been killed");
            });
        }
    }

    static async Task CreateTable(string connectionString)
    {
        try
        {
            await using var dbConnection = new MySqlConnection(connectionString);
            dbConnection.Open();

            var createBansTable = @"
            CREATE TABLE IF NOT EXISTS `miniadmin_bans` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `AdminUsername` VARCHAR(255) NOT NULL,
                `AdminSteamId` VARCHAR(255) NOT NULL,
                `Username` VARCHAR(255) NOT NULL,
                `SteamId64` BIGINT NOT NULL,
                `SteamId` VARCHAR(255) NOT NULL,
                `Reason` VARCHAR(255) NOT NULL,
                `UnbanReason` VARCHAR(255) NOT NULL,
                `AdminUnlockedUsername` VARCHAR(255) NOT NULL,
                `AdminUnlockedSteamId` VARCHAR(255) NOT NULL,
                `StartBanTime` BIGINT NOT NULL,
                `EndBanTime` BIGINT NOT NULL,
                `BanActive` BOOLEAN NOT NULL
            );";

            await dbConnection.ExecuteAsync(createBansTable);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    static async Task CreateAdminsTable(string connectionString)
    {
        try
        {
            await using var connection = new MySqlConnection(connectionString);

            connection.Open();

            var createAdminsTable = @"
                CREATE TABLE IF NOT EXISTS `miniadmin_admins` (
                `Id` INT AUTO_INCREMENT PRIMARY KEY,
                `Username` VARCHAR(255) NOT NULL,
                `SteamId` VARCHAR(255) NOT NULL,
                `StartTime` BIGINT NOT NULL,
                `EndTime` BIGINT NOT NULL
            );";

            await connection.ExecuteAsync(createAdminsTable);
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }
    }

    [ConsoleCommand("css_who")]
    public void OnCmdWho(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller != null) return;

        var maxNameLength = 0;

        var id = 0;
        foreach (var client in Utilities.GetPlayers())
        {
            var playerName = !string.IsNullOrWhiteSpace(client.PlayerName) ? client.PlayerName : "unknown";
            var playerNameLength = playerName.Length;
            maxNameLength = Math.Max(maxNameLength, playerNameLength);

            var adminStatus = IsAdmin(client) ? "admin " : "player";

            var index = client.Index!;
            var playTime = DateTime.Now - _playerPlayTime[index]; 

            id++;
            var formattedOutput =
                $"{id,-1} - {playerName,-15} | {adminStatus,-6} | Playtime: {playTime.Hours:D2}:{playTime.Minutes:D2}:{playTime.Seconds:D2}";

            PrintToServer(formattedOutput, ConsoleColor.Magenta);
        }
    }

    private static CSSTargetResult? GetTarget(CommandInfo info)
    {
        var matches = info.GetArgTargetResult(1);

        if (!matches.Any())
        {
            info.ReplyToCommand($"Target {info.GetArg(1)} not found.");
            return null;
        }

        if (!(matches.Count() > 1) || info.GetArg(1).StartsWith('@'))
            return matches;

        info.ReplyToCommand($"Multiple targets found for \"{info.GetArg(1)}\".");

        return null;
    }


    [ConsoleCommand("css_ban", "ban a player")]
    [CommandHelper(1, "<#userid or name> <time_minutes> <reason>")]
    [RequiresPermissions("@css/ban")]
    public void OnCmdBan(CCSPlayerController? controller, CommandInfo command)
    {
        var cmdArg = command.ArgString;


        if (command.ArgCount is < 4 or > 4)
        {
            ReplyToCommand(controller, "Using: css_ban <userid> <time_minutes> <reason>");
            return;
        }

        var splitCmdArgs = Regex.Matches(cmdArg, @"[\""].+?[\""]|[^ ]+")
            .Select(m => m.Value)
            .ToArray();

        // var convertCmdArg = Convert.ToInt32(ExtractValueInQuotes(command.GetArg(1)));

        var target = GetTarget(command);
        //var userId = NativeAPI.GetUseridFromIndex(convertCmdArg + 1);

        target?.Players.ForEach(player =>
        {
            if (!AdminManager.CanPlayerTarget(controller, player))
            {
                command.ReplyToCommand("You can't target this player.");
                return;
            }

            var endBanTime = Convert.ToInt32(ExtractValueInQuotes(command.GetArg(2)));
            var reason = ExtractValueInQuotes(command.GetArg(3));

            var startBanTimeUnix = DateTime.UtcNow.GetUnixEpoch();
            var endBanTimeUnix = DateTime.UtcNow.AddMinutes(endBanTime).GetUnixEpoch();

            Server.PrintToConsole($"start: {startBanTimeUnix}, end: {endBanTimeUnix}, user {player.PlayerName}, id {player.SteamID}, reason {reason}, minutes {endBanTime}");

            var msg = AddBan(new User
            {
                AdminUsername = controller != null ? controller.PlayerName : "Console",
                AdminSteamId = controller != null ? new SteamID(controller.SteamID).SteamId2 : "Console",
                Username = player.PlayerName,
                SteamId64 = player.SteamID,
                SteamId = new SteamID(player.SteamID).SteamId2,
                Reason = reason,
                UnbanReason = "",
                AdminUnlockedUsername = "",
                AdminUnlockedSteamId = "",
                StartBanTime = startBanTimeUnix,
                EndBanTime = endBanTime == 0 ? 0 : endBanTimeUnix,
                BanActive = true
            }).Result;

            Server.ExecuteCommand($"kick {player.PlayerName}");

            ReplyToCommand(controller, msg);

           
        });

        
    }

    [ConsoleCommand("css_addadmin")]
    public void OnCmdAddAdmin(CCSPlayerController? controller, CommandInfo command)
    {
        if(controller != null) return;
    
        var cmdArg = command.ArgString;

        if (command.ArgCount is < 4 or > 4)
        {
            PrintToServer("Using: css_addadmin <username> <steamid> <time_seconds>", ConsoleColor.Red);
            return;
        }
        
        var splitCmdArgs = Regex.Matches(cmdArg, @"[\""].+?[\""]|[^ ]+")
            .Select(m => m.Value)
            .ToArray();

        var username = ExtractValueInQuotes(splitCmdArgs[0]);
        var steamId = ExtractValueInQuotes(splitCmdArgs[1]);
        var endTime = Convert.ToInt32(ExtractValueInQuotes(splitCmdArgs[2]));
        
        var startTimeUnix = DateTime.UtcNow.GetUnixEpoch();
        var endTimeUnix = DateTime.UtcNow.AddSeconds(endTime).GetUnixEpoch();

        var msg = AddAdmin(new Admins
        {
            Username = username,
            SteamId = steamId,
            StartTime = startTimeUnix,
            EndTime = endTime == 0 ? 0 : endTimeUnix
        }).Result;

        PrintToServer(msg, ConsoleColor.Green);
    }

    private async Task<string> AddBan(User user)
    {
        try
        {
            if (IsUserBanned(user.SteamId))
                return $"The user with the SteamId identifier {user.SteamId} has already been banned.";

            await using var connection = new MySqlConnection(_dbConnectionString);

            await connection.ExecuteAsync(@"
                INSERT INTO miniadmin_bans (AdminUsername, AdminSteamId, Username, SteamId64, SteamId, Reason, UnbanReason, AdminUnlockedUsername, AdminUnlockedSteamId, StartBanTime, EndBanTime, BanActive)
                VALUES (@AdminUsername, @AdminSteamId, @Username, @SteamId64, @SteamId, @Reason, @UnbanReason, @AdminUnlockedUsername, @AdminUnlockedSteamId, @StartBanTime, @EndBanTime, @BanActive);
                ", user);

            return $"Player '{user.Username} | [{user.SteamId}]' is banned";
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return string.Empty;
    }

    private async Task<string> AddAdmin(Admins admin)
    {
        try
        {
            if (IsAdminExist(admin.SteamId))
                return $"An administrator with the SteamId identifier {admin.SteamId} already exists.";

            await using var connection = new MySqlConnection(_dbConnectionString);

            await connection.ExecuteAsync(@"INSERT INTO miniadmin_admins (Username, SteamId, StartTime, EndTime)
                        VALUES (@Username, @SteamId, @StartTime, @EndTime);", admin);

            return $"Admin '{admin.Username}[{admin.SteamId}]' successfully added";
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return string.Empty;
    }

    [ConsoleCommand("css_unban", "unban")]
    [CommandHelper(1, "<SteamId> <Reason>")]
    [RequiresPermissions("@css/ban")]
    public void OnCmdUnban(CCSPlayerController? controller, CommandInfo command)
    {
        var cmdArg = command.ArgString;
        

        if (command.ArgCount is < 3 or > 3)
        {
            ReplyToCommand(controller, "Using: css_unban <SteamId> <Reason>");
            return;
        }
        
        var splitCmdArgs = Regex.Matches(cmdArg, @"[\""].+?[\""]|[^ ]+")
            .Select(m => m.Value)
            .ToArray();

        var steamId = ExtractValueInQuotes(splitCmdArgs[0]);
        var reason = ExtractValueInQuotes(splitCmdArgs[1]);

        var msg = UnbanUser(
            controller != null ? controller.PlayerName : "Console",
            controller != null ? new SteamID(controller.SteamID).SteamId2 : "Console",
            steamId, reason).Result;
    
        ReplyToCommand(controller, msg); 
    }

    [ConsoleCommand("css_deleteadmin", "delete admin")]
    public void OnCmdDeleteAdmin(CCSPlayerController? controller, CommandInfo command)
    {
        if (controller != null) return;
        
        var cmdArg = command.ArgString;

        if (command.ArgCount is < 2 or > 2)
        {
            PrintToServer("Using: css_deleteadmin <SteamId>", ConsoleColor.Red);
            return;
        }

        var steamId = ExtractValueInQuotes(cmdArg);
        
        var msg = DeleteAdmin(steamId).Result;

        PrintToServer(msg, ConsoleColor.Green);
    }

    private async Task<string> DeleteAdmin(string steamId)
    {
        try
        {
            await using var connection = new MySqlConnection(_dbConnectionString);

            await connection.ExecuteAsync(@"DELETE FROM miniadmin_admins WHERE SteamId = @SteamId;",
                new { SteamId = steamId });

            return $"Admin {steamId} successfully deleted";
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return string.Empty;
    }

    private async Task<string> UnbanUser(string adminName, string adminSteamId, string steamId, string reason)
    {
        try 
        {
            // Server.PrintToConsole("u1");
            using var connection = new MySqlConnection(_dbConnectionString);
            // Server.PrintToConsole("u2");
            var user = connection.QueryFirstOrDefault<User>(
                "SELECT * FROM miniadmin_bans WHERE (SteamId = @SteamId OR Username = @UserName) AND BanActive = 1",
                new { SteamId = steamId, UserName = steamId });
            // Server.PrintToConsole("u3");
            if (user == null) return "User not found or not currently banned";
            // Server.PrintToConsole("u4");
            user.UnbanReason = reason;
            user.AdminUnlockedUsername = adminName;
            user.AdminUnlockedSteamId = adminSteamId;
            user.BanActive = false;
            // Server.PrintToConsole("u5");
            connection.Execute(@"
                    UPDATE miniadmin_bans
                    SET UnbanReason = @UnbanReason, AdminUnlockedUsername = @AdminUnlockedUsername,
                        AdminUnlockedSteamId = @AdminUnlockedSteamId, BanActive = @BanActive
                    WHERE SteamId = @SteamId AND BanActive = 1
                    ", user);
            // Server.PrintToConsole("u6");
            return $"Player {steamId} has been successfully unblocked with reason: {reason}";
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        return string.Empty;
    }

    private Config LoadConfig()
    {
        var configPath = Path.Combine(ModuleDirectory, "database.json");
        if (!File.Exists(configPath)) return CreateConfig(configPath);

        var config = JsonSerializer.Deserialize<Config>(File.ReadAllText(configPath))!;

        return config;
    }

    private string BuildConnectionString()
    {
        var dbConfig = LoadConfig();

        Console.WriteLine("Building connection string");
        var builder = new MySqlConnectionStringBuilder
        {
            Database = dbConfig.Connection.Database,
            UserID = dbConfig.Connection.User,
            Password = dbConfig.Connection.Password,
            Server = dbConfig.Connection.Host,
            Port = 3306,
        };

        Console.WriteLine("OK!");
        return builder.ConnectionString;
    }

    private Config CreateConfig(string configPath)
    {
        var config = new Config
        {
            Connection = new MiniAdminDb
            {
                Host = "",
                Database = "",
                User = "",
                Password = ""
            }
        };

        File.WriteAllText(configPath,
            JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true }));
        return config;
    }

    private string ExtractValueInQuotes(string input)
    {
        var match = Regex.Match(input, @"""([^""]*)""");

        return match.Success ? match.Groups[1].Value : input;
    }

    private void ReplyToCommand(CCSPlayerController? controller, string msg)
    {
        if (controller != null)
            PrintToChat(controller, msg);
        else
            PrintToServer(msg, ConsoleColor.DarkMagenta);
    }

    private void ChangeMap(string mapName)
    {
        Server.ExecuteCommand($"map {mapName}");
    }

    private void KickClient(string userId)
    {
        Server.ExecuteCommand($"kickid {userId}");
    }

    private void PrintToChat(CCSPlayerController controller, string msg)
    {
        controller.PrintToChat($"\x08[ \x0CMiniAdmin \x08] {msg}");
    }

    private void PrintToChatAll(string msg)
    {
        Server.PrintToChatAll($"\x08[ \x0CMiniAdmin \x08] {msg}");
    }

    private void PrintToConsole(CCSPlayerController client, string msg)
    {
        client.PrintToConsole(msg);
    }

    private void PrintToServer(string msg, ConsoleColor color)
    {
        Console.ForegroundColor = color;
        Console.WriteLine($"[ MiniAdmin ] {msg}");
        Console.ResetColor();
    }

    private bool IsAdmin(CCSPlayerController controller)
    {
        var steamId = new SteamID(controller.SteamID).SteamId2;

        using var connection = new MySqlConnection(_dbConnectionString);

        var admin = connection.QueryFirstOrDefault<Admins>(
            "SELECT * FROM miniadmin_admins WHERE SteamId = @SteamId",
            new { SteamId = steamId });

        return admin != null;
    }

    private bool IsUserBanned(string steamId)
    {
        using var connection = new MySqlConnection(_dbConnectionString);

        var existingBan = connection.QueryFirstOrDefault<User>(
            "SELECT * FROM miniadmin_bans WHERE SteamId = @SteamId AND BanActive = 1",
            new { SteamId = steamId });

        return existingBan != null;
    }

    private bool IsAdminExist(string steamId)
    {
        using var connection = new MySqlConnection(_dbConnectionString);

        var existingAdmin = connection.QueryFirstOrDefault<Admins>(
            "SELECT * FROM miniadmin_admins WHERE SteamId = @SteamId",
            new { SteamId = steamId });

        return existingAdmin != null;
    }
}

public static class GetUnixTime
{
    public static int GetUnixEpoch(this DateTime dateTime)
    {
        var unixTime = dateTime.ToUniversalTime() - 
                       new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        return (int)unixTime.TotalSeconds;
    }
}

public class Config
{
    public MiniAdminDb Connection { get; set; } = null!;
}

public class MiniAdminDb
{
    public required string Host { get; init; }
    public required string Database { get; init; }
    public required string User { get; init; }
    public required string Password { get; init; }
}

public class User
{
    public required string AdminUsername { get; set; }
    public required string AdminSteamId { get; set; }
    public required string Username { get; set; }
    public ulong SteamId64 { get; set; }
    public required string SteamId { get; set; }
    public required string Reason { get; set; }
    public required string UnbanReason { get; set; }
    public required string AdminUnlockedUsername { get; set; }
    public required string AdminUnlockedSteamId { get; set; }
    public int StartBanTime { get; set; }
    public int EndBanTime { get; set; }
    public bool BanActive { get; set; }
}

public class Admins
{
    public required string Username { get; set; }
    public required string SteamId { get; set; }
    public int StartTime { get; set; }
    public int EndTime { get; set; }
}