﻿/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using Meebey.SmartIrc4net;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public static class CommandHandler
    {
        public static readonly Dictionary<string, Action<CommandArguments>> Commands = new Dictionary<string, Action<CommandArguments>>
        {
            { "!help", OnCommandHelp },
            { "!app", OnCommandApp },
            { "!sub", OnCommandPackage },
            { "!players", OnCommandPlayers },
            { "!bins", OnCommandBinaries },
            { "!reload", OnCommandReload },
            { "!force", OnCommandForce },
            { "!debug", OnCommanDebug }
        };

        public class CommandArguments
        {
            public string[] MessageArray { get; set; }
            public string Channel { get; set; }
            public string Nickname { get; set; }
            public SteamID ChatRoomID { get; set; }
            public SteamID SenderID { get; set; }

            public bool IsChatRoomCommand
            {
                get
                {
                    return this.ChatRoomID != null;
                }
            }
        }

        public static void ReplyToCommand(CommandArguments command, string message, params object[] args)
        {
            if (command.IsChatRoomCommand)
            {
                Steam.Instance.Friends.SendChatRoomMessage(command.ChatRoomID, EChatEntryType.ChatMsg, string.Format(":dsham: {0}", Colors.StripColors(string.Format(message, args))));
            }
            else
            {
                IRC.Instance.Client.SendMessage(SendType.Message, command.Channel, string.Format(message, args), Priority.High);
            }
        }

        public static void OnChannelMessage(object sender, IrcEventArgs e)
        {
            if (e.Data.Message[0] != '!')
            {
                return;
            }

            if (e.Data.Message == "!relogin" && IRC.IsSenderOp(e.Data.Channel, e.Data.Nick))
            {
                if (Steam.Instance.Client.IsConnected)
                {
                    Steam.Instance.Client.Disconnect();
                }

                foreach (var idler in Program.GCIdlers)
                {
                    if (idler.Client.IsConnected)
                    {
                        idler.Client.Disconnect();
                    }
                }

                Log.WriteInfo("IRC", "Relogin forced by user {0} in channel {1}", e.Data.Nick, e.Data.Channel);

                IRC.Send(e.Data.Channel, "You're responsible for death of everything and everyone now.");
            }

            Action<CommandArguments> callbackFunction;

            if (Commands.TryGetValue(e.Data.MessageArray[0], out callbackFunction))
            {
                var command = new CommandArguments
                {
                    Channel = e.Data.Channel,
                    Nickname = e.Data.Nick,
                    MessageArray = e.Data.MessageArray
                };

                if (!Steam.Instance.Client.IsConnected)
                {
                    ReplyToCommand(command, "{0}{1}{2}: Not connected to Steam.", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                    return;
                }

                if (SteamDB.IsBusy())
                {
                    ReplyToCommand(command, "{0}{1}{2}: The bot is currently busy.", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                    return;
                }

                Log.WriteInfo("IRC", "Handling command {0} for user {1} in channel {2}", e.Data.MessageArray[0], e.Data.Nick, e.Data.Channel);

                callbackFunction(command);
            }
        }

        private static void OnCommanDebug(CommandArguments command)
        {
            var response = string.Format("ProcessorPool: {0}/{1} threads, SecondaryPool: {2}/{3} threads, DepotProcessor: {4}/{5} threads, ProcessedApps: {6}, ProcessedSubs: {7}",
                Steam.Instance.ProcessorPool.InUseThreads,
                Steam.Instance.ProcessorPool.ActiveThreads,
                Steam.Instance.SecondaryPool.InUseThreads,
                Steam.Instance.SecondaryPool.ActiveThreads,
                DepotProcessor.ThreadPool.InUseThreads,
                DepotProcessor.ThreadPool.ActiveThreads,
                Steam.Instance.ProcessedApps.Count,
                Steam.Instance.ProcessedSubs.Count
            );

            ReplyToCommand(command, response);
        }

        private static void OnCommandHelp(CommandArguments command)
        {
            ReplyToCommand(command, "{0}{1}{2}: Available commands: {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, string.Join(string.Format("{0}, {1}", Colors.NORMAL, Colors.OLIVE), Commands.Keys));
        }

        private static void OnCommandApp(CommandArguments command)
        {
            if (command.MessageArray.Length < 2)
            {
                ReplyToCommand(command, "Usage:{0} !app <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(command.MessageArray[1], out appID))
            {
                var apps = new List<uint>();

                apps.Add(appID);

                var jobID = Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Type = SteamProxy.IRCRequestType.TYPE_APP,
                    Command = command
                });
            }
            else
            {
                string name = string.Format("%{0}%", string.Join(" ", command.MessageArray.Skip(1)).Trim());

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` WHERE `Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
                {
                    if (Reader.Read())
                    {
                        appID = Reader.GetUInt32("AppID");

                        var apps = new List<uint>();

                        apps.Add(appID);

                        var jobID = Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                            {
                                JobID = jobID,
                                Target = appID,
                                Type = SteamProxy.IRCRequestType.TYPE_APP,
                                Command = command
                            });
                    }
                    else
                    {
                        ReplyToCommand(command, "{0}{1}{2}: Nothing was found matching your request", Colors.OLIVE, command.Nickname, Colors.NORMAL);
                    }
                }
            }
        }

        private static void OnCommandPackage(CommandArguments command)
        {
            uint subID;

            if (command.MessageArray.Length >= 2 && uint.TryParse(command.MessageArray[1], out subID))
            {
                var jobID = Steam.Instance.Apps.PICSGetProductInfo(null, subID, false, false);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = subID,
                    Type = SteamProxy.IRCRequestType.TYPE_SUB,
                    Command = command
                });
            }
            else
            {
                ReplyToCommand(command, "Usage:{0} !sub <subid>", Colors.OLIVE);
            }
        }

        private static void OnCommandPlayers(CommandArguments command)
        {
            if (command.MessageArray.Length < 2)
            {
                ReplyToCommand(command, "Usage:{0} !players <appid or partial game name>", Colors.OLIVE);

                return;
            }

            uint appID;

            if (uint.TryParse(command.MessageArray[1], out appID))
            {
                var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                {
                    JobID = jobID,
                    Target = appID,
                    Command = command
                });
            }
            else
            {
                string name = string.Format("%{0}%", string.Join(" ", command.MessageArray.Skip(1)).Trim());

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `Apps` LEFT JOIN `AppsTypes` ON `Apps`.`AppType` = `AppsTypes`.`AppType` WHERE `AppsTypes`.`Name` IN ('game', 'application') AND (`Apps`.`StoreName` LIKE @Name OR `Apps`.`Name` LIKE @Name) ORDER BY `LastUpdated` DESC LIMIT 1", new MySqlParameter("Name", name)))
                {
                    if (Reader.Read())
                    {
                        appID = Reader.GetUInt32("AppID");

                        var jobID = Steam.Instance.UserStats.GetNumberOfCurrentPlayers(appID);

                        SteamProxy.Instance.IRCRequests.Add(new SteamProxy.IRCRequest
                        {
                            JobID = jobID,
                            Target = appID,
                            Command = command
                        });
                    }
                    else
                    {
                        ReplyToCommand(command, "{0}{1}{2}: Nothing was found matching your request", Colors.OLIVE, command.Nickname, Colors.NORMAL);
                    }
                }
            }
        }

        private static void OnCommandBinaries(CommandArguments command)
        {
            if (command.IsChatRoomCommand || !IRC.IsSenderOp(command.Channel, command.Nickname))
            {
                return;
            }

            string cdn = "http://media.steampowered.com/client/";

            using (var webClient = new WebClient())
            {
                var manifest = webClient.DownloadData(string.Format("{0}steam_client_publicbeta_osx?_={1}", cdn, DateTime.UtcNow.Ticks));

                var kv = new KeyValue();

                using (var ms = new MemoryStream(manifest))
                {
                    try
                    {
                        kv.ReadAsText(ms);
                    }
                    catch
                    {
                        ReplyToCommand(command, "{0}{1}{2}: Something went horribly wrong and keyvalue parser died", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                        return;
                    }
                }

                if (kv["bins_osx"].Children.Count == 0)
                {
                    ReplyToCommand(command, "{0}{1}{2}: Failed to find binaries in parsed response.", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                    return;
                }

                kv = kv["bins_osx"];

                ReplyToCommand(command, "{0}{1}{2}:{3} {4}{5} {6}({7} MB)", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.DARK_BLUE, cdn, kv["file"].AsString(), Colors.DARK_GRAY, (kv["size"].AsLong() / 1048576.0).ToString("0.###"));
            }
        }

        private static void OnCommandReload(CommandArguments command)
        {
            if (command.IsChatRoomCommand)
            {
                ReplyToCommand(command, "{0}: This command can only be used in IRC", command.Nickname);

                return;
            }

            if (IRC.IsSenderOp(command.Channel, command.Nickname))
            {
                SteamProxy.Instance.ReloadImportant(command.Channel, command.Nickname);
            }
        }

        private static void OnCommandForce(CommandArguments command)
        {
            if (command.IsChatRoomCommand)
            {
                ReplyToCommand(command, "{0}: This command can only be used in IRC", command.Nickname);

                return;
            }

            if (!IRC.IsSenderOp(command.Channel, command.Nickname))
            {
                return;
            }

            if (command.MessageArray.Length >= 3)
            {
                uint target;

                if (!uint.TryParse(command.MessageArray[2], out target))
                {
                    ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                    return;
                }

                switch (command.MessageArray[1])
                {
                    case "app":
                    {
                        var apps = new List<uint>();

                        apps.Add(target);

                        Steam.Instance.Apps.PICSGetAccessTokens(apps, Enumerable.Empty<uint>());

                        ReplyToCommand(command, "{0}{1}{2}: Forced update for AppID {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }

                    case "sub":
                    {
                        Steam.Instance.Apps.PICSGetProductInfo(null, target, false, false);

                        ReplyToCommand(command, "{0}{1}{2}: Forced update for SubID {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }

#if DEBUG
                    case "changelist":
                    {
                        if (Math.Abs(Steam.Instance.PreviousChange - target) > 100)
                        {
                            ReplyToCommand(command, "Changelist difference is too big, will not execute");

                            break;
                        }

                        Steam.Instance.PreviousChange = target;

                        Steam.Instance.GetPICSChanges();

                        ReplyToCommand(command, "{0}{1}{2}: Requested changes since changelist {3}{4}", Colors.OLIVE, command.Nickname, Colors.NORMAL, Colors.OLIVE, target);

                        break;
                    }
#endif

                    default:
                    {
                        ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);

                        break;
                    }
                }
            }
            else if (command.MessageArray.Length == 1)
            {
                Steam.Instance.GetPICSChanges();

                ReplyToCommand(command, "{0}{1}{2}: Forced a check", Colors.OLIVE, command.Nickname, Colors.NORMAL);
            }
            else
            {
                ReplyToCommand(command, "Usage:{0} !force [<app/sub/changelist> <target>]", Colors.OLIVE);
            }
        }
    }
}
