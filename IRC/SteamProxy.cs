/*
 * Copyright (c) 2013, SteamDB. All rights reserved.
 * Use of this source code is governed by a BSD-style license that can be
 * found in the LICENSE file.
 */
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MySql.Data.MySqlClient;
using SteamKit2;

namespace SteamDatabaseBackend
{
    public class SteamProxy
    {
        private static SteamProxy _instance = new SteamProxy();
        public static SteamProxy Instance { get { return _instance; } }

        public enum IRCRequestType
        {
            TYPE_APP,
            TYPE_SUB
        }

        public class IRCRequest
        {
            public CommandHandler.CommandArguments Command { get; set; }
            public IRCRequestType Type { get; set; }
            public JobID JobID { get; set; }
            public uint Target { get; set; }
            public uint DepotID { get; set; }
            public SteamID SteamID { get; set; }
        }

        private static readonly SteamID SteamLUG = new SteamID(103582791431044413UL);
        private static readonly string ChannelSteamLUG = "#steamlug";

        public List<IRCRequest> IRCRequests { get; private set; }
        public List<uint> ImportantApps { get; private set; }
        private List<uint> ImportantSubs;

        public SteamProxy()
        {
            IRCRequests = new List<IRCRequest>();

            ImportantApps = new List<uint>();
            ImportantSubs = new List<uint>();
        }

        public void ReloadImportant(string channel = "", string nickName = "")
        {
            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Announce` = 1"))
            {
                ImportantApps.Clear();

                while (Reader.Read())
                {
                    ImportantApps.Add(Reader.GetUInt32("AppID"));
                }
            }

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `SubID` FROM `ImportantSubs`"))
            {
                ImportantSubs.Clear();

                while (Reader.Read())
                {
                    ImportantSubs.Add(Reader.GetUInt32("SubID"));
                }
            }

            if (string.IsNullOrEmpty(channel))
            {
                Log.WriteInfo("IRC Proxy", "Loaded {0} important apps and {1} packages", ImportantApps.Count, ImportantSubs.Count);
            }
            else
            {
                IRC.Send(channel, "{0}{1}{2}: Reloaded {3} important apps and {4} packages", Colors.OLIVE, nickName, Colors.NORMAL, ImportantApps.Count, ImportantSubs.Count);
            }
        }

        public static string GetPackageName(uint subID)
        {
            string name = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Subs` WHERE `SubID` = @SubID", new MySqlParameter("SubID", subID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);

                    if (name.StartsWith("Steam Sub", StringComparison.Ordinal))
                    {
                        string nameStore = DbWorker.GetString("StoreName", Reader);

                        if (!string.IsNullOrEmpty(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                        }
                    }
                }
            }

            return name;
        }

        public static string GetAppName(uint appID)
        {
            string name = string.Empty;
            string nameStore = string.Empty;

            using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `Name`, `StoreName` FROM `Apps` WHERE `AppID` = @AppID", new MySqlParameter("AppID", appID)))
            {
                if (Reader.Read())
                {
                    name = DbWorker.GetString("Name", Reader);
                    nameStore = DbWorker.GetString("StoreName", Reader);
                }
            }

            if (string.IsNullOrEmpty(name) || name.StartsWith("ValveTestApp", StringComparison.Ordinal) || name.StartsWith("SteamDB Unknown App", StringComparison.Ordinal))
            {
                if (!string.IsNullOrEmpty(nameStore))
                {
                    return string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `NewValue` FROM `AppsHistory` WHERE `AppID` = @AppID AND `Action` = 'created_info' AND `Key` = 1 LIMIT 1", new MySqlParameter("AppID", appID)))
                {
                    if (Reader.Read())
                    {
                        nameStore = DbWorker.GetString("NewValue", Reader);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("AppID {0}", appID);
                        }

                        if (!name.Equals(nameStore))
                        {
                            name = string.Format("{0} {1}({2}){3}", name, Colors.DARK_GRAY, nameStore, Colors.NORMAL);
                        }
                    }
                }
            }

            return name;
        }

        public void OnChatMemberInfo(SteamFriends.ChatMemberInfoCallback callback)
        {
            // If we get kicked, rejoin the chatroom
            if (callback.Type == EChatInfoType.StateChange && callback.StateChangeInfo.ChatterActedOn == Steam.Instance.Client.SteamID)
            {
                Log.WriteInfo("Steam", "State changed for chatroom {0} to {1}", callback.ChatRoomID, callback.StateChangeInfo.StateChange);

                if (callback.StateChangeInfo.StateChange == EChatMemberStateChange.Disconnected
                ||  callback.StateChangeInfo.StateChange == EChatMemberStateChange.Kicked
                ||  callback.StateChangeInfo.StateChange == EChatMemberStateChange.Left)
                {
                    Steam.Instance.Friends.JoinChat(callback.ChatRoomID);
                }
            }
        }

        public void OnChatMessage(SteamFriends.ChatMsgCallback callback)
        {
            if (callback.ChatMsgType != EChatEntryType.ChatMsg || callback.Message[0] != '!' || callback.Message.Contains('\n'))
            {
                return;
            }

            Action<CommandHandler.CommandArguments> callbackFunction;
            var messageArray = callback.Message.Split(' ');

            if (CommandHandler.Commands.TryGetValue(messageArray[0], out callbackFunction))
            {
                var command = new CommandHandler.CommandArguments
                {
                    SenderID = callback.ChatterID,
                    ChatRoomID = callback.ChatRoomID,
                    Nickname = Steam.Instance.Friends.GetFriendPersonaName(callback.ChatterID),
                    MessageArray = messageArray
                };

                if (SteamDB.IsBusy())
                {
                    CommandHandler.ReplyToCommand(command, "{0}{1}{2}: The bot is currently busy.", Colors.OLIVE, command.Nickname, Colors.NORMAL);

                    return;
                }

                Log.WriteInfo("Steam", "Handling command {0} for user {1} in chatroom {2}", messageArray[0], callback.ChatterID, callback.ChatRoomID);

                callbackFunction(command);
            }
        }

        public void OnClanState(SteamFriends.ClanStateCallback callback)
        {
            if (callback.Events.Count == 0 && callback.Announcements.Count == 0)
            {
                return;
            }

            string groupName = callback.ClanName;
            string message;

            if (string.IsNullOrEmpty(groupName))
            {
                groupName = Steam.Instance.Friends.GetClanName(callback.ClanID);

                // Check once more, because that can fail too
                if (string.IsNullOrEmpty(groupName))
                {
                    groupName = "Group";

                    Log.WriteError("IRC Proxy", "ClanID: {0} - no group name", callback.ClanID);
                }
            }

            foreach (var announcement in callback.Announcements)
            {
                message = string.Format("{0}{1}{2} announcement: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/announcements/detail/{8}",
                                            Colors.OLIVE, groupName, Colors.NORMAL,
                                            Colors.GREEN, announcement.Headline, Colors.NORMAL,
                                            Colors.DARK_BLUE, callback.ClanID, announcement.ID
                                        );

                IRC.SendMain(message);

                // Additionally send announcements to steamlug channel
                if (callback.ClanID.Equals(SteamLUG))
                {
                    IRC.Send(ChannelSteamLUG, message);
                }

                Log.WriteInfo("Group Announcement", "{0} \"{1}\"", groupName, announcement.Headline);
            }

            foreach (var groupEvent in callback.Events)
            {
                if (groupEvent.JustPosted)
                {
                    message = string.Format("{0}{1}{2} event: {3}{4}{5} -{6} http://steamcommunity.com/gid/{7}/events/{8} {9}({10})",
                                                Colors.OLIVE, groupName, Colors.NORMAL,
                                                Colors.GREEN, groupEvent.Headline,Colors.NORMAL,
                                                Colors.DARK_BLUE, callback.ClanID, groupEvent.ID,
                                                Colors.DARK_GRAY, groupEvent.EventTime.ToString()
                                            );

                    // Send events only to steamlug channel
                    if (callback.ClanID.Equals(SteamLUG))
                    {
                        IRC.Send(ChannelSteamLUG, message);
                    }
                    else
                    {
                        IRC.SendMain(message);
                    }

                    Log.WriteInfo("Group Announcement", "{0} Event \"{1}\"", groupName, groupEvent.Headline);
                }
            }
        }

        public void OnNumberOfPlayers(SteamUserStats.NumberOfPlayersCallback callback, JobID jobID)
        {
            var request = IRCRequests.Find(r => r.JobID == jobID);

            if (request == null)
            {
                return;
            }

            IRCRequests.Remove(request);

            if (callback.Result != EResult.OK)
            {
                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Unable to request player count: {3}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, callback.Result);
            }
            else if (request.Target == 0)
            {
                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: {3}{4:N0}{5} people praising lord Gaben right now, influence:{6} {7}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, Colors.OLIVE, callback.NumPlayers, Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetGraphURL(0));
            }
            else
            {
                string url;
                string name = GetAppName(request.Target);

                if (string.IsNullOrEmpty(name))
                {
                    name = string.Format("AppID {0}", request.Target);
                }

                using (MySqlDataReader Reader = DbWorker.ExecuteReader("SELECT `AppID` FROM `ImportantApps` WHERE `Graph` = 1 AND `AppID` = @AppID", new MySqlParameter("AppID", request.Target)))
                {
                    if (Reader.Read())
                    {
                        url = string.Format("{0} - graph:{1} {2}", Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetGraphURL(request.Target));
                    }
                    else
                    {
                        url = string.Format("{0} -{1} {2}", Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetAppURL(request.Target));
                    }
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: People playing {3}{4}{5} right now: {6}{7:N0}{8}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, Colors.OLIVE, name, Colors.NORMAL, Colors.YELLOW, callback.NumPlayers, url);
            }
        }

        public void OnProductInfo(IRCRequest request, SteamApps.PICSProductInfoCallback callback)
        {
            if (request.Type == SteamProxy.IRCRequestType.TYPE_SUB)
            {
                if (!callback.Packages.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Unknown SubID: {3}{4}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Packages[request.Target];
                var kv = info.KeyValues.Children.FirstOrDefault(); // Blame VoiDeD
                string name = string.Format("SubID {0}", info.ID);

                if (kv["name"].Value != null)
                {
                    name = kv["name"].AsString();
                }

                try
                {
                    kv.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "sub", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Dump for {3}{4}{5} -{6} {7}{8}{9}",
                                              Colors.OLIVE, request.Command.Nickname, Colors.NORMAL,
                                              Colors.OLIVE, name, Colors.NORMAL,
                                              Colors.DARK_BLUE, SteamDB.GetRawPackageURL(info.ID), Colors.NORMAL,
                                              info.MissingToken ? " (mising token)" : string.Empty
                );
            }
            else if (request.Type == SteamProxy.IRCRequestType.TYPE_APP)
            {
                if (!callback.Apps.ContainsKey(request.Target))
                {
                    CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Unknown AppID: {3}{4}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, Colors.OLIVE, request.Target);

                    return;
                }

                var info = callback.Apps[request.Target];
                string name = string.Format("AppID {0}", info.ID);

                if (info.KeyValues["common"]["name"].Value != null)
                {
                    name = info.KeyValues["common"]["name"].AsString();
                }

                try
                {
                    info.KeyValues.SaveToFile(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app", string.Format("{0}.vdf", info.ID)), false);
                }
                catch (Exception e)
                {
                    CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Unable to save file for {3}: {4}", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL, name, e.Message);

                    return;
                }

                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: Dump for {3}{4}{5} -{6} {7}{8}{9}",
                                              Colors.OLIVE, request.Command.Nickname, Colors.NORMAL,
                                              Colors.OLIVE, name, Colors.NORMAL,
                                              Colors.DARK_BLUE, SteamDB.GetRawAppURL(info.ID), Colors.NORMAL,
                                              info.MissingToken ? " (mising token)" : string.Empty
                );
            }
            else
            {
                CommandHandler.ReplyToCommand(request.Command, "{0}{1}{2}: I have no idea what happened here!", Colors.OLIVE, request.Command.Nickname, Colors.NORMAL);
            }
        }

        public void OnPICSChanges(SteamApps.PICSChangesCallback callback)
        {
            // Print any apps importants first
            var important = callback.AppChanges.Keys.Intersect(ImportantApps);

            if (important.Count() > 5)
            {
                IRC.SendMain("{0}{1}{2} important apps updated -{3} {4}", Colors.OLIVE, important.Count(), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetChangelistURL(callback.CurrentChangeNumber));
            }
            else
            {
                foreach (var app in important)
                {
                    IRC.SendMain("Important app update: {0}{1}{2} -{3} {4}", Colors.OLIVE, GetAppName(app), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetAppURL(app, "history"));
                }
            }

            // And then important packages
            important = callback.PackageChanges.Keys.Intersect(ImportantSubs);

            if (important.Count() > 5)
            {
                IRC.SendMain("{0}{1}{2} important packages updated -{3} {4}", Colors.OLIVE, important.Count(), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetChangelistURL(callback.CurrentChangeNumber));
            }
            else
            {
                foreach (var package in important)
                {
                    IRC.SendMain("Important package update: {0}{1}{2} -{3} {4}", Colors.OLIVE, GetPackageName(package), Colors.NORMAL, Colors.DARK_BLUE, SteamDB.GetPackageURL(package, "history"));
                }
            }

            // Group apps and package changes by changelist, this will seperate into individual changelists
            var appGrouping = callback.AppChanges.Values.GroupBy(a => a.ChangeNumber);
            var packageGrouping = callback.PackageChanges.Values.GroupBy(p => p.ChangeNumber);

            // Join apps and packages back together based on changelist number
            var changeLists = Utils.FullOuterJoin(appGrouping, packageGrouping, a => a.Key, p => p.Key, (a, p, key) => new
                {
                    ChangeNumber = key,

                    Apps = a.ToList(),
                    Packages = p.ToList(),
                },
                new EmptyGrouping<uint, SteamApps.PICSChangesCallback.PICSChangeData>(),
                new EmptyGrouping<uint, SteamApps.PICSChangesCallback.PICSChangeData>())
                .OrderBy(c => c.ChangeNumber);

            foreach (var changeList in changeLists)
            {
                var appCount = changeList.Apps.Count;
                var packageCount = changeList.Packages.Count;

                string Message = string.Format("Changelist {0}{1}{2} {3}({4:N0} apps and {5:N0} packages){6} -{7} {8}",
                    Colors.OLIVE, changeList.ChangeNumber, Colors.NORMAL,
                    Colors.DARK_GRAY, appCount, packageCount, Colors.NORMAL,
                    Colors.DARK_BLUE, SteamDB.GetChangelistURL(changeList.ChangeNumber)
                );

                if (appCount >= 50 || packageCount >= 50)
                {
                    IRC.SendMain(Message);
                }

                IRC.SendAnnounce("{0}»{1} {2}", Colors.RED, Colors.NORMAL, Message);

                // If this changelist is very big, freenode will hate us forever if we decide to print all that stuff
                if (appCount + packageCount > 500)
                {
                    IRC.SendAnnounce("{0}  This changelist is too big to be printed in IRC, please view it on our website", Colors.RED);

                    continue;
                }

                string name;

                if (appCount > 0)
                {
                    foreach (var app in changeList.Apps)
                    {
                        name = GetAppName(app.ID);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("{0}{1}{2}", Colors.GREEN, app.ID, Colors.NORMAL);
                        }
                        else
                        {
                            name = string.Format("{0}{1}{2} - {3}", Colors.LIGHT_GRAY, app.ID, Colors.NORMAL, name);
                        }

                        IRC.SendAnnounce("  App: {0}{1}", name, app.NeedsToken ? string.Format(" {0}(needs token){1}", Colors.RED, Colors.NORMAL) : string.Empty);
                    }
                }

                if (packageCount > 0)
                {
                    foreach (var package in changeList.Packages)
                    {
                        name = GetPackageName(package.ID);

                        if (string.IsNullOrEmpty(name))
                        {
                            name = string.Format("{0}{1}{2}", Colors.GREEN, package.ID, Colors.NORMAL);
                        }
                        else
                        {
                            name = string.Format("{0}{1}{2} - {3}", Colors.LIGHT_GRAY, package.ID, Colors.NORMAL, name);
                        }

                        IRC.SendAnnounce("  Package: {0}{1}", name, package.NeedsToken ? string.Format(" {0}(needs token){1}", Colors.RED, Colors.NORMAL) : string.Empty);
                    }
                }
            }
        }
    }
}
