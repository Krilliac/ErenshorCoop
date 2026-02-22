using System;
using System.Collections.Generic;
using System.Linq;
using ErenshorDedicatedServer.Core;
using ErenshorDedicatedServer.Data;
using ErenshorDedicatedServer.Network;
using LiteNetLib;
using LiteNetLib.Utils;

namespace ErenshorDedicatedServer.World
{
    /// <summary>
    /// Manages party/group system. Handles invites, accepts, declines, member lists,
    /// member removal, and group XP sharing.
    /// </summary>
    public class GroupManager
    {
        private readonly NetworkManager _network;
        private readonly Dictionary<int, Group> _groups = new();
        private readonly Dictionary<short, int> _playerToGroup = new();
        private readonly Dictionary<short, PendingInvite> _pendingInvites = new();
        private int _nextGroupId = 1;
        private readonly object _groupLock = new object();

        public GroupManager(NetworkManager network)
        {
            _network = network ?? throw new ArgumentNullException(nameof(network));
        }

        public void HandleGroupPacket(PlayerSession session, NetDataReader reader)
        {
            if (session == null || reader == null) return;

            try
            {
                var entityId = reader.GetShort();
                var flag = reader.GetUShort();
                var dataTypes = PacketHelper.ReadSubTypeFlag<GroupDataType>(flag);

                if (dataTypes.Contains(GroupDataType.INVITE))
                    HandleInvite(session, reader);
                else if (dataTypes.Contains(GroupDataType.ACCEPT_DECLINE))
                    HandleAcceptDecline(session, reader);
                else if (dataTypes.Contains(GroupDataType.REMOVE))
                    HandleRemove(session, reader);
                else if (dataTypes.Contains(GroupDataType.INVITE_SIM))
                    HandleSimInvite(session, reader);
                else if (dataTypes.Contains(GroupDataType.EXPERIENCE))
                    HandleExperience(session, reader);
                else if (dataTypes.Contains(GroupDataType.SIM_FOLLOW))
                    HandleSimFollow(session, reader);
                else
                {
                    // Relay as-is for other group sub-types
                    RelayGroupPacketToMembers(session, reader);
                }
            }
            catch (Exception ex)
            {
                ServerLogger.Error($"Error handling group packet from [{session.PlayerId}]: {ex.Message}", "GROUP");
            }
        }

        private void HandleInvite(PlayerSession session, NetDataReader reader)
        {
            var targetId = reader.GetShort();
            var targetSession = _network.GetSession(targetId);

            if (targetSession == null)
            {
                ServerLogger.Debug($"Group invite: target [{targetId}] not found", "GROUP");
                return;
            }

            lock (_groupLock)
            {
                // Check if target is already in a group
                if (_playerToGroup.ContainsKey(targetId))
                {
                    ServerLogger.Debug($"Group invite: [{targetId}] already in group", "GROUP");
                    return;
                }

                // Store pending invite
                _pendingInvites[targetId] = new PendingInvite
                {
                    InviterId = session.PlayerId,
                    InviterName = session.CharacterName,
                    InviteTime = DateTime.UtcNow
                };
            }

            // Send invite to target player
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_GROUP);
            writer.Put(session.PlayerId);
            var flags = new HashSet<GroupDataType> { GroupDataType.INVITE_RESPONSE };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));
            writer.Put(session.PlayerId);

            _network.SendTo(targetSession, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.SERVER_GROUP));

            ServerLogger.Debug($"[{session.PlayerId}] {session.CharacterName} invited [{targetId}] {targetSession.CharacterName} to group", "GROUP");
        }

        private void HandleAcceptDecline(PlayerSession session, NetDataReader reader)
        {
            var accepted = reader.GetBool();

            lock (_groupLock)
            {
                if (!_pendingInvites.TryGetValue(session.PlayerId, out var invite))
                    return;

                _pendingInvites.Remove(session.PlayerId);

                if (!accepted)
                {
                    ServerLogger.Debug($"[{session.PlayerId}] declined group invite from [{invite.InviterId}]", "GROUP");
                    return;
                }

                // Find or create group for the inviter
                int groupId;
                if (_playerToGroup.TryGetValue(invite.InviterId, out var existingGroupId))
                {
                    groupId = existingGroupId;
                }
                else
                {
                    groupId = _nextGroupId++;
                    _groups[groupId] = new Group { GroupId = groupId, LeaderId = invite.InviterId };
                    _groups[groupId].Members.Add(invite.InviterId);
                    _playerToGroup[invite.InviterId] = groupId;

                    var inviterSession = _network.GetSession(invite.InviterId);
                    if (inviterSession != null)
                        inviterSession.GroupId = groupId;
                }

                // Add the accepting player
                if (!_groups[groupId].Members.Contains(session.PlayerId))
                    _groups[groupId].Members.Add(session.PlayerId);
                _playerToGroup[session.PlayerId] = groupId;
                session.GroupId = groupId;

                ServerLogger.Info($"[{session.PlayerId}] {session.CharacterName} joined group {groupId}", "GROUP");

                // Broadcast updated member list to all group members
                BroadcastMemberList(groupId);
            }
        }

        private void HandleRemove(PlayerSession session, NetDataReader reader)
        {
            var targetId = reader.GetShort();
            var reason = reader.GetByte(); // 0 = left, 1 = kicked

            lock (_groupLock)
            {
                if (!_playerToGroup.TryGetValue(session.PlayerId, out var groupId))
                    return;

                if (!_groups.TryGetValue(groupId, out var group))
                    return;

                // Only leader can kick others
                if (targetId != session.PlayerId && group.LeaderId != session.PlayerId)
                    return;

                RemoveFromGroup(targetId, groupId, reason == 1);
            }
        }

        private void HandleSimInvite(PlayerSession session, NetDataReader reader)
        {
            // Relay sim invites to group members
            RelayGroupPacketToMembers(session, reader);
        }

        private void HandleExperience(PlayerSession session, NetDataReader reader)
        {
            // Relay XP packets to group
            RelayGroupPacketToMembers(session, reader);
        }

        private void HandleSimFollow(PlayerSession session, NetDataReader reader)
        {
            // Relay sim follow to group
            RelayGroupPacketToMembers(session, reader);
        }

        // ====================================================
        // GROUP OPERATIONS
        // ====================================================

        public void RemoveFromGroup(short playerId, int groupId, bool kicked)
        {
            lock (_groupLock)
            {
                if (!_groups.TryGetValue(groupId, out var group))
                    return;

                group.Members.Remove(playerId);
                _playerToGroup.Remove(playerId);

                var playerSession = _network.GetSession(playerId);
                if (playerSession != null)
                    playerSession.GroupId = -1;

                // Notify removed player
                SendGroupRemovePacket(playerId, kicked);

                ServerLogger.Info($"[{playerId}] {(kicked ? "kicked from" : "left")} group {groupId}", "GROUP");

                if (group.Members.Count <= 1)
                {
                    // Disband
                    foreach (var memberId in group.Members.ToArray())
                    {
                        _playerToGroup.Remove(memberId);
                        var memberSession = _network.GetSession(memberId);
                        if (memberSession != null)
                            memberSession.GroupId = -1;
                        SendGroupRemovePacket(memberId, false);
                    }
                    _groups.Remove(groupId);
                    ServerLogger.Debug($"Group {groupId} disbanded", "GROUP");
                }
                else
                {
                    // Transfer leadership if leader left
                    if (group.LeaderId == playerId && group.Members.Count > 0)
                        group.LeaderId = group.Members[0];

                    BroadcastMemberList(groupId);
                }
            }
        }

        public void OnPlayerDisconnect(short playerId)
        {
            lock (_groupLock)
            {
                _pendingInvites.Remove(playerId);

                if (_playerToGroup.TryGetValue(playerId, out var groupId))
                {
                    RemoveFromGroup(playerId, groupId, false);
                }
            }
        }

        // ====================================================
        // BROADCAST
        // ====================================================

        private void BroadcastMemberList(int groupId)
        {
            lock (_groupLock)
            {
                if (!_groups.TryGetValue(groupId, out var group)) return;

                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.SERVER_GROUP);
                writer.Put((short)0); // entity ID
                var flags = new HashSet<GroupDataType> { GroupDataType.MEMBER_LIST };
                writer.Put(PacketHelper.GetSubTypeFlag(flags));
                writer.Put(group.Members.Count);
                foreach (var memberId in group.Members)
                {
                    writer.Put(memberId);
                    writer.Put(memberId == group.LeaderId);
                }

                foreach (var memberId in group.Members)
                {
                    _network.SendTo(memberId, writer, DeliveryMethod.ReliableOrdered,
                        PacketHelper.GetChannel(PacketType.SERVER_GROUP));
                }
            }
        }

        private void SendGroupRemovePacket(short playerId, bool kicked)
        {
            var writer = new NetDataWriter();
            writer.Put((byte)PacketType.SERVER_GROUP);
            writer.Put(playerId);
            var flags = new HashSet<GroupDataType> { GroupDataType.REMOVE };
            writer.Put(PacketHelper.GetSubTypeFlag(flags));
            writer.Put(playerId);
            writer.Put((byte)(kicked ? 1 : 0));

            _network.SendTo(playerId, writer, DeliveryMethod.ReliableOrdered,
                PacketHelper.GetChannel(PacketType.SERVER_GROUP));
        }

        private void RelayGroupPacketToMembers(PlayerSession session, NetDataReader reader)
        {
            lock (_groupLock)
            {
                if (!_playerToGroup.TryGetValue(session.PlayerId, out var groupId)) return;
                if (!_groups.TryGetValue(groupId, out var group)) return;

                var writer = new NetDataWriter();
                writer.Put((byte)PacketType.GROUP);
                writer.Put(reader.RawData, 1, reader.RawDataSize - 1);

                foreach (var memberId in group.Members)
                {
                    if (memberId != session.PlayerId)
                    {
                        _network.SendTo(memberId, writer, DeliveryMethod.ReliableOrdered,
                            PacketHelper.GetChannel(PacketType.GROUP));
                    }
                }
            }
        }

        // ====================================================
        // INFO
        // ====================================================

        public Group GetGroup(int groupId)
        {
            lock (_groupLock)
            {
                _groups.TryGetValue(groupId, out var group);
                return group;
            }
        }

        public int GetGroupCount()
        {
            lock (_groupLock) { return _groups.Count; }
        }

        public List<Group> GetAllGroups()
        {
            lock (_groupLock) { return _groups.Values.ToList(); }
        }
    }

    public class Group
    {
        public int GroupId { get; set; }
        public short LeaderId { get; set; }
        public List<short> Members { get; set; } = new();
    }

    public class PendingInvite
    {
        public short InviterId { get; set; }
        public string InviterName { get; set; }
        public DateTime InviteTime { get; set; }
    }
}
