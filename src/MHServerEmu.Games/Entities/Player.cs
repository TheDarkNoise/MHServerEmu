﻿using System.Text;
using Gazillion;
using Google.ProtocolBuffers;
using MHServerEmu.Core.Extensions;
using MHServerEmu.Core.Logging;
using MHServerEmu.Core.Serialization;
using MHServerEmu.Core.System;
using MHServerEmu.DatabaseAccess.Models;
using MHServerEmu.Games.Achievements;
using MHServerEmu.Games.Common;
using MHServerEmu.Games.Entities.Avatars;
using MHServerEmu.Games.Entities.Inventories;
using MHServerEmu.Games.Entities.Options;
using MHServerEmu.Games.GameData;
using MHServerEmu.Games.GameData.Prototypes;
using MHServerEmu.Games.Missions;
using MHServerEmu.Games.Network;
using MHServerEmu.Games.Properties;
using MHServerEmu.Games.Regions;
using MHServerEmu.Games.Regions.MatchQueues;
using MHServerEmu.Games.Social.Communities;
using MHServerEmu.Games.Social.Guilds;

namespace MHServerEmu.Games.Entities
{
    // Avatar index for console versions that have local coop, mostly meaningless on PC.
    public enum PlayerAvatarIndex       
    {
        Primary,
        Secondary,
        Count
    }

    // NOTE: These badges and their descriptions are taken from an internal build dated June 2015 (most likely version 1.35).
    // They are not fully implemented and they may be outdated for our version 1.52.
    public enum AvailableBadges
    {
        CanGrantBadges = 1,         // User can grant badges to other users
        SiteCommands,               // User can run the site commands (player/regions lists, change to specific region etc)
        CanBroadcastChat,           // User can send a chat message to all players
        AllContentAccess,           // User has access to all content in the game
        CanLogInAsAnotherAccount,   // User has ability to log in as another account
        CanDisablePersistence,      // User has ability to play without saving
        PlaytestCommands,           // User can always use commands that are normally only available during a playtest (e.g. bug)
        CsrUser,                    // User can perform Customer Service Representative commands
        DangerousCheatAccess,       // User has access to some especially dangerous cheats
        NumberOfBadges
    }

    public class Player : Entity, IMissionManagerOwner
    {
        private static readonly Logger Logger = LogManager.CreateLogger();

        private MissionManager _missionManager = new();
        private ReplicatedPropertyCollection _avatarProperties = new();
        private ulong _shardId;
        private ReplicatedVariable<string> _playerName = new();
        private ulong[] _consoleAccountIds = new ulong[(int)PlayerAvatarIndex.Count];
        private ReplicatedVariable<string> _secondaryPlayerName = new();
        private MatchQueueStatus _matchQueueStatus = new();

        // NOTE: EmailVerified and AccountCreationTimestamp are set in NetMessageGiftingRestrictionsUpdate that
        // should be sent in the packet right after logging in. NetMessageGetCurrencyBalanceResponse should be
        // sent along with it.
        private bool _emailVerified;
        private TimeSpan _accountCreationTimestamp;     // UnixTime

        private ReplicatedVariable<ulong> _partyId = new();

        private ulong _guildId;
        private string _guildName;
        private GuildMembership _guildMembership;

        private Community _community;
        private List<PrototypeId> _unlockedInventoryList = new();
        private SortedSet<AvailableBadges> _badges = new();
        private GameplayOptions _gameplayOptions = new();
        private AchievementState _achievementState = new();
        private Dictionary<PrototypeId, StashTabOptions> _stashTabOptionsDict = new();

        // Accessors
        public MissionManager MissionManager { get => _missionManager; }
        public ulong ShardId { get => _shardId; }
        public MatchQueueStatus MatchQueueStatus { get => _matchQueueStatus; }
        public bool EmailVerified { get => _emailVerified; set => _emailVerified = value; }
        public TimeSpan AccountCreationTimestamp { get => _accountCreationTimestamp; set => _accountCreationTimestamp = value; }
        public override ulong PartyId { get => _partyId.Value; }
        public Community Community { get => _community; }
        public GameplayOptions GameplayOptions { get => _gameplayOptions; }
        public AchievementState AchievementState { get => _achievementState; }

        
        // Avatars
        public Avatar CurrentAvatar { get; private set; }
        public List<Avatar> AvatarList { get; } = new();    // temp until we implement inventories

        // new
        public Player(Game game): base(game) { }

        // old
        public Player(EntityBaseData baseData) : base(baseData)
        {
            // Base Data
            BaseData.ReplicationPolicy = AOINetworkPolicyValues.AOIChannelOwner;
            BaseData.EntityId = 14646212;
            BaseData.PrototypeId = (PrototypeId)18307315963852687724;
            BaseData.FieldFlags = EntityCreateMessageFlags.HasInterestPolicies | EntityCreateMessageFlags.HasDbId;
            BaseData.InterestPolicies = AOINetworkPolicyValues.AOIChannelOwner;
            BaseData.DbId = 867587;
            BaseData.LocomotionState = new(0f);

            // Archive Data
            ReplicationPolicy = AOINetworkPolicyValues.AOIChannelOwner;
            Properties.ReplicationId = 9078332;

            _missionManager.Owner = this;
            _avatarProperties.ReplicationId = 9078333;
            _shardId = 3;
            _playerName = new(9078334, string.Empty);
            _secondaryPlayerName = new(0, string.Empty);
            _matchQueueStatus.SetOwner(this);
            _partyId = new(9078335, 0);
            _community = new(this);
            _community.Initialize();
            _gameplayOptions.SetOwner(this);
        }

        public Player(EntityBaseData baseData, ByteString archiveData) : base(baseData, archiveData)
        {
            _missionManager.Owner = this;
            _matchQueueStatus.SetOwner(this);
            _gameplayOptions.SetOwner(this);
        }

        public override bool Serialize(Archive archive)
        {
            bool success = base.Serialize(archive);

            success &= Serializer.Transfer(archive, ref _missionManager);
            success &= Serializer.Transfer(archive, ref _avatarProperties);

            // archive.IsTransient
            success &= Serializer.Transfer(archive, ref _shardId);
            success &= Serializer.Transfer(archive, ref _playerName);
            success &= Serializer.Transfer(archive, ref _consoleAccountIds[0]);
            success &= Serializer.Transfer(archive, ref _consoleAccountIds[1]);
            success &= Serializer.Transfer(archive, ref _secondaryPlayerName);
            success &= Serializer.Transfer(archive, ref _matchQueueStatus);
            success &= Serializer.Transfer(archive, ref _emailVerified);
            success &= Serializer.Transfer(archive, ref _accountCreationTimestamp);

            // archive.IsReplication
            success &= Serializer.Transfer(archive, ref _partyId);
            success &= GuildMember.SerializeReplicationRuntimeInfo(archive, ref _guildId, ref _guildName, ref _guildMembership);

            // There is a string here that is always empty and is immediately discarded after reading, purpose unknown
            string emptyString = string.Empty;
            success &= Serializer.Transfer(archive, ref emptyString);
            if (emptyString != string.Empty) Logger.Warn($"Serialize(): emptyString is not empty!");

            //bool hasCommunityData = archive.IsPersistent || archive.IsMigration
            //    || (archive.IsReplication && ((AOINetworkPolicyValues)archive.ReplicationPolicy).HasFlag(AOINetworkPolicyValues.AOIChannelOwner));
            bool hasCommunityData = true;
            success &= Serializer.Transfer(archive, ref hasCommunityData);
            if (hasCommunityData)
                success &= Serializer.Transfer(archive, ref _community);

            // Unknown bool, always false
            bool unkBool = false;
            success &= Serializer.Transfer(archive, ref unkBool);
            if (unkBool) Logger.Warn($"Serialize(): unkBool is true!");

            success &= Serializer.Transfer(archive, ref _unlockedInventoryList);

            //if (archive.IsMigration || (archive.IsReplication && ((AOINetworkPolicyValues)archive.ReplicationPolicy).HasFlag(AOINetworkPolicyValues.AOIChannelOwner)))
            success &= Serializer.Transfer(archive, ref _badges);

            success &= Serializer.Transfer(archive, ref _gameplayOptions);

            //if (archive.IsMigration || (archive.IsReplication && ((AOINetworkPolicyValues)archive.ReplicationPolicy).HasFlag(AOINetworkPolicyValues.AOIChannelOwner)))
            success &= Serializer.Transfer(archive, ref _achievementState);

            success &= Serializer.Transfer(archive, ref _stashTabOptionsDict);

            return success;
        }

        protected override void Decode(CodedInputStream stream)
        {
            base.Decode(stream);

            BoolDecoder boolDecoder = new();

            _missionManager.Decode(stream, boolDecoder);
            _avatarProperties.Decode(stream);

            _shardId = stream.ReadRawVarint64();

            _playerName.Decode(stream);
            _consoleAccountIds[0] = stream.ReadRawVarint64();
            _consoleAccountIds[1] = stream.ReadRawVarint64();
            _secondaryPlayerName.Decode(stream);

            _matchQueueStatus.Decode(stream);

            _emailVerified = boolDecoder.ReadBool(stream);
            _accountCreationTimestamp = new(stream.ReadRawInt64() * 10);

            _partyId.Decode(stream);

            GuildMember.SerializeReplicationRuntimeInfo(stream, boolDecoder, ref _guildId, ref _guildName, ref _guildMembership);

            // There is a string here that is always empty and is immediately discarded after reading, purpose unknown
            if (stream.ReadRawString() != string.Empty)
                Logger.Warn($"Decode(): emptyString is not empty!");

            _community = new(this);
            _community.Initialize();
            bool hasCommunityData = boolDecoder.ReadBool(stream);
            if (hasCommunityData) Community.Decode(stream);

            // Unknown bool, always false
            if (boolDecoder.ReadBool(stream))
                Logger.Warn($"Decode(): unkBool is true!");

            _unlockedInventoryList.Clear();
            ulong numUnlockedInventories = stream.ReadRawVarint64();
            for (ulong i = 0; i < numUnlockedInventories; i++)
                _unlockedInventoryList.Add(stream.ReadPrototypeRef<Prototype>());

            _badges.Clear();
            ulong numBadges = stream.ReadRawVarint64();
            for (ulong i = 0; i < numBadges; i++)
                _badges.Add((AvailableBadges)stream.ReadRawVarint32());

            _gameplayOptions.Decode(stream, boolDecoder);

            _achievementState.Decode(stream);

            _stashTabOptionsDict.Clear();
            ulong numStashTabOptions = stream.ReadRawVarint64();
            for (ulong i = 0; i < numStashTabOptions; i++)
            {
                PrototypeId stashTabRef = stream.ReadPrototypeRef<Prototype>();
                StashTabOptions options = new();
                options.Decode(stream);
                _stashTabOptionsDict.Add(stashTabRef, options);
            }
        }

        public override void Encode(CodedOutputStream stream)
        {
            base.Encode(stream);

            // Prepare bool encoder
            BoolEncoder boolEncoder = new();

            _missionManager.EncodeBools(boolEncoder);

            boolEncoder.EncodeBool(_emailVerified);
            boolEncoder.EncodeBool(_guildId != GuildMember.InvalidGuildId);
            boolEncoder.EncodeBool(true);   // hasCommunity TODO: Check archive's replication policy and send community only to owners
            boolEncoder.EncodeBool(false);  // Unknown unused bool, always false

            GameplayOptions.EncodeBools(boolEncoder);

            boolEncoder.Cook();

            // Encode
            _missionManager.Encode(stream, boolEncoder);
            _avatarProperties.Encode(stream);

            stream.WriteRawVarint64(_shardId);
            _playerName.Encode(stream);
            stream.WriteRawVarint64(_consoleAccountIds[0]);
            stream.WriteRawVarint64(_consoleAccountIds[1]);
            _secondaryPlayerName.Encode(stream);
            _matchQueueStatus.Encode(stream);
            boolEncoder.WriteBuffer(stream);   // EmailVerified
            stream.WriteRawInt64(AccountCreationTimestamp.Ticks / 10);
            _partyId.Encode(stream);
            GuildMember.SerializeReplicationRuntimeInfo(stream, boolEncoder, ref _guildId, ref _guildName, ref _guildMembership);
            stream.WriteRawString(string.Empty);    // Mysterious always empty throwaway string

            boolEncoder.WriteBuffer(stream);   // hasCommunity
            _community.Encode(stream);

            boolEncoder.WriteBuffer(stream);   // UnkBool

            stream.WriteRawVarint64((ulong)_unlockedInventoryList.Count);
            foreach (PrototypeId unlockedInventory in _unlockedInventoryList)
                stream.WritePrototypeRef<Prototype>(unlockedInventory);

            stream.WriteRawVarint64((ulong)_badges.Count);
            foreach (AvailableBadges badge in _badges)
                stream.WriteRawVarint32((uint)badge);

            GameplayOptions.Encode(stream, boolEncoder);

            AchievementState.Encode(stream);

            stream.WriteRawVarint64((ulong)_stashTabOptionsDict.Count);
            foreach (var kvp in _stashTabOptionsDict)
            {
                stream.WritePrototypeRef<Prototype>(kvp.Key);
                kvp.Value.Encode(stream);
            }
        }

        /// <summary>
        /// Initializes this <see cref="Player"/> from data contained in the provided <see cref="DBAccount"/>.
        /// </summary>
        public void InitializeFromDBAccount(DBAccount account)
        {
            // Adjust properties
            foreach (var accountAvatar in account.Avatars.Values)
            {
                var avatarPrototypeRef = (PrototypeId)accountAvatar.RawPrototype;

                // Set library costumes according to account data
                Properties[PropertyEnum.AvatarLibraryCostume, 0, avatarPrototypeRef] = (PrototypeId)accountAvatar.RawCostume;

                // Set avatar levels to 60
                // Note: setting this to above level 60 sets the prestige level as well
                Properties[PropertyEnum.AvatarLibraryLevel, 0, avatarPrototypeRef] = 60;
            }

            foreach (PrototypeId avatarRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<AvatarPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                if (avatarRef == (PrototypeId)6044485448390219466) continue;   //zzzBrevikOLD.prototype
                Properties[PropertyEnum.AvatarUnlock, avatarRef] = (int)AvatarUnlockType.Type2;
            }

            foreach (PrototypeId waypointRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<WaypointPrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.Waypoint, waypointRef] = true;

            foreach (PrototypeId vendorRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<VendorTypePrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.VendorLevel, vendorRef] = 1;

            foreach (PrototypeId uiSystemLockRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<UISystemLockPrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.UISystemLock, uiSystemLockRef] = true;

            foreach (PrototypeId tutorialRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<HUDTutorialPrototype>(PrototypeIterateFlags.NoAbstract))
                Properties[PropertyEnum.TutorialHasSeenTip, tutorialRef] = true;

            // TODO: Set this after creating all avatar entities via a NetMessageSetProperty in the same packet
            Properties[PropertyEnum.PlayerMaxAvatarLevel] = 60;

            // Complete all missions
            _missionManager.SetAvatar((PrototypeId)account.CurrentAvatar.RawPrototype);
            foreach (PrototypeId missionRef in GameDatabase.DataDirectory.IteratePrototypesInHierarchy<MissionPrototype>(PrototypeIterateFlags.NoAbstractApprovedOnly))
            {
                var missionPrototype = GameDatabase.GetPrototype<MissionPrototype>(missionRef);
                if (_missionManager.ShouldCreateMission(missionPrototype))
                {
                    Mission mission = _missionManager.CreateMission(missionRef);
                    mission.SetState(MissionState.Completed);
                    mission.AddParticipant(this);
                    _missionManager.InsertMission(mission);
                }
            }

            // Set name
            _playerName.Value = account.PlayerName;    // NOTE: This is used for highlighting your name in leaderboards

            // Todo: send this separately in NetMessageGiftingRestrictionsUpdate on login
            Properties[PropertyEnum.LoginCount] = 1075;
            _emailVerified = true;
            _accountCreationTimestamp = Clock.DateTimeToUnixTime(new(2023, 07, 16, 1, 48, 0));   // First GitHub commit date

            #region Hardcoded social tab easter eggs
            _community.AddMember(1, "DavidBrevik", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(1).SetIsOnline(1)
                .SetCurrentRegionRefId(12735255224807267622).SetCurrentDifficultyRefId((ulong)DifficultyTier.Normal)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(15769648016960461069).SetCostumeRefId(4881398219179434365).SetLevel(60).SetPrestigeLevel(6))
                .Build());

            _community.AddMember(2, "TonyStark", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(2).SetIsOnline(1)
                .SetCurrentRegionRefId((ulong)RegionPrototypeId.NPEAvengersTowerHUBRegion).SetCurrentDifficultyRefId((ulong)DifficultyTier.Normal)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(421791326977791218).SetCostumeRefId(7150542631074405762).SetLevel(60).SetPrestigeLevel(5))
                .Build());

            _community.AddMember(3, "Doomsaw", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(3).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(17750839636937086083).SetCostumeRefId(14098108758769669917).SetLevel(60).SetPrestigeLevel(6))
                .Build());

            _community.AddMember(4, "PizzaTime", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(4).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(9378552423541970369).SetCostumeRefId(6454902525769881598).SetLevel(60).SetPrestigeLevel(5))
                .Build());

            _community.AddMember(5, "RogueServerEnjoyer", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(5).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(1660250039076459846).SetCostumeRefId(9447440487974639491).SetLevel(60).SetPrestigeLevel(3))
                .Build());

            _community.AddMember(6, "WhiteQueenXOXO", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(6).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(412966192105395660).SetCostumeRefId(12724924652099869123).SetLevel(60).SetPrestigeLevel(4))
                .Build());

            _community.AddMember(7, "AlexBond", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(7).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(9255468350667101753).SetCostumeRefId(16813567318560086134).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(8, "Crypto137", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(8).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(421791326977791218).SetCostumeRefId(1195778722002966150).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(9, "yn01", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(9).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(12534955053251630387).SetCostumeRefId(14506515434462517197).SetLevel(60).SetPrestigeLevel(2))
                .Build());

            _community.AddMember(10, "Gazillion", CircleId.__Friends);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(10).SetIsOnline(0).Build());

            _community.AddMember(11, "FriendlyLawyer", CircleId.__Nearby);
            _community.ReceiveMemberBroadcast(CommunityMemberBroadcast.CreateBuilder().SetMemberPlayerDbId(11).SetIsOnline(1)
                .AddSlots(CommunityMemberAvatarSlot.CreateBuilder().SetAvatarRefId(12394659164528645362).SetCostumeRefId(2844257346122946366).SetLevel(99).SetPrestigeLevel(1))
                .Build());
            #endregion

            // Initialize and unlock stash tabs
            OnEnterGameInitStashTabOptions();
            foreach (PrototypeId stashRef in GetStashInventoryProtoRefs(true, false))
                UnlockInventory(stashRef);

            // Add all badges to admin accounts
            if (account.UserLevel == AccountUserLevel.Admin)
            {
                for (var badge = AvailableBadges.CanGrantBadges; badge < AvailableBadges.NumberOfBadges; badge++)
                    AddBadge(badge);
            }

            _gameplayOptions.ResetToDefaults();
        }

        public void SaveToDBAccount(DBAccount account)
        {
            account.Player.RawAvatar = (long)CurrentAvatar.EntityPrototype.DataRef;
            foreach (Avatar avatar in AvatarList)
            {
                DBAvatar dbAvatar = account.GetAvatar((long)avatar.BaseData.PrototypeId);
                dbAvatar.RawCostume = avatar.Properties[PropertyEnum.CostumeCurrent];

                // Encode key mapping
                var abilityKeyMapping = avatar.AbilityKeyMappings[0];

                BoolEncoder boolEncoder = new();
                boolEncoder.EncodeBool(abilityKeyMapping.ShouldPersist);
                boolEncoder.Cook();

                using (MemoryStream ms = new())
                {
                    CodedOutputStream cos = CodedOutputStream.CreateInstance(ms);
                    abilityKeyMapping.Encode(cos, boolEncoder);
                    cos.Flush();
                    dbAvatar.RawAbilityKeyMapping = ms.ToArray();
                }
            }
        }

        /// <summary>
        /// Returns the name of the player for the specified <see cref="PlayerAvatarIndex"/>.
        /// </summary>
        public string GetName(PlayerAvatarIndex avatarIndex = PlayerAvatarIndex.Primary)
        {
            if ((avatarIndex >= PlayerAvatarIndex.Primary && avatarIndex < PlayerAvatarIndex.Count) == false)
                Logger.Warn("GetName(): avatarIndex out of range");

            if (avatarIndex == PlayerAvatarIndex.Secondary)
                return _secondaryPlayerName.Value;

            return _playerName.Value;
        }

        /// <summary>
        /// Returns the console account id for the specified <see cref="PlayerAvatarIndex"/>.
        /// </summary>
        public ulong GetConsoleAccountId(PlayerAvatarIndex avatarIndex)
        {
            if ((avatarIndex >= PlayerAvatarIndex.Primary && avatarIndex < PlayerAvatarIndex.Count) == false)
                return 0;

            return _consoleAccountIds[(int)avatarIndex];
        }

        /// <summary>
        /// Returns <see langword="true"/> if the inventory with the specified <see cref="PrototypeId"/> is unlocked for this <see cref="Player"/>.
        /// </summary>
        public bool IsInventoryUnlocked(PrototypeId invProtoRef)
        {
            if (invProtoRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, $"IsInventoryUnlocked(): invProtoRef == PrototypeId.Invalid");

            return _unlockedInventoryList.Contains(invProtoRef);
        }

        /// <summary>
        /// Unlocks the inventory with the specified <see cref="PrototypeId"/> for this <see cref="Player"/>.
        /// </summary>
        public bool UnlockInventory(PrototypeId invProtoRef)
        {
            // Entity::GetInventoryByRef()

            if (_unlockedInventoryList.Contains(invProtoRef))
                return Logger.WarnReturn(false, $"UnlockInventory(): {GameDatabase.GetFormattedPrototypeName(invProtoRef)} is already unlocked");

            _unlockedInventoryList.Add(invProtoRef);

            // Entity::addInventory()

            if (Inventory.IsPlayerStashInventory(invProtoRef))
                StashTabInsert(invProtoRef, 0);

            return true;
        }

        /// <summary>
        /// Returns <see cref="PrototypeId"/> values of all locked and/or unlocked stash tabs for this <see cref="Player"/>.
        /// </summary>
        public IEnumerable<PrototypeId> GetStashInventoryProtoRefs(bool getLocked, bool getUnlocked)
        {
            var playerProto = GameDatabase.GetPrototype<PlayerPrototype>(BaseData.PrototypeId);
            if (playerProto == null) yield break;
            if (playerProto.StashInventories == null) yield break;

            foreach (EntityInventoryAssignmentPrototype invAssignmentProto in playerProto.StashInventories)
            {
                if (invAssignmentProto.Inventory == PrototypeId.Invalid) continue;

                // TODO: isLocked = Entity::GetInventory() == null
                // For now use prototype data + unlock list for this
                var inventoryProto = GameDatabase.GetPrototype<InventoryPrototype>(invAssignmentProto.Inventory);
                bool isLocked = true;
                isLocked &= inventoryProto.LockedByDefault;
                isLocked &= _unlockedInventoryList.Contains(inventoryProto.DataRef) == false;
                // Although the unified stash from the console version is unlocked by default, we consider it always locked on PC
                isLocked |= inventoryProto.ConvenienceLabel == ConvenienceLabel.UnifiedStash;

                if (isLocked && getLocked || isLocked == false && getUnlocked)
                    yield return invAssignmentProto.Inventory;
            }
        }

        /// <summary>
        /// Updates <see cref="StashTabOptions"/> with the data from a <see cref="NetMessageStashTabOptions"/>.
        /// </summary>
        public bool UpdateStashTabOptions(NetMessageStashTabOptions optionsMessage)
        {
            PrototypeId inventoryRef = (PrototypeId)optionsMessage.InventoryRefId;

            if (Inventory.IsPlayerStashInventory(inventoryRef) == false)
                return Logger.WarnReturn(false, $"UpdateStashTabOptions(): {inventoryRef} is not a player stash ref");

            // Entity::GetInventoryByRef() != nullptr

            if (_stashTabOptionsDict.TryGetValue(inventoryRef, out StashTabOptions options) == false)
            {
                options = new();
                _stashTabOptionsDict.Add(inventoryRef, options);
            }

            // Stash tab names can be up to 30 characters long
            if (optionsMessage.HasDisplayName)
                options.DisplayName = optionsMessage.DisplayName.Substring(0, 30);

            if (optionsMessage.HasIconPathAssetId)
                options.IconPathAssetId = (AssetId)optionsMessage.IconPathAssetId;

            if (optionsMessage.HasColor)
                options.Color = (StashTabColor)optionsMessage.Color;

            return true;
        }

        /// <summary>
        /// Inserts the stash tab with the specified <see cref="PrototypeId"/> into the specified position.
        /// </summary>
        public bool StashTabInsert(PrototypeId insertedStashRef, int newSortOrder)
        {
            if (newSortOrder < 0)
                return Logger.WarnReturn(false, $"StashTabInsert(): Invalid newSortOrder {newSortOrder}");

            if (insertedStashRef == PrototypeId.Invalid)
                return Logger.WarnReturn(false, $"StashTabInsert(): Invalid insertedStashRef {insertedStashRef}");

            if (Inventory.IsPlayerStashInventory(insertedStashRef) == false)
                return Logger.WarnReturn(false, $"StashTabInsert(): insertedStashRef {insertedStashRef} is not a player stash ref");

            // Entity::GetInventoryByRef(insertedStashRef) != nullptr

            // Get options for the tab we need to insert
            if (_stashTabOptionsDict.TryGetValue(insertedStashRef, out StashTabOptions options))
            {
                // Only new tabs are allowed to be in the same location
                if (options.SortOrder == newSortOrder)
                    return Logger.WarnReturn(false, "StashTabInsert(): Inserting an existing tab at the same location");
            }
            else
            {
                // Create options of the tab if there are none yet
                options = new();
                _stashTabOptionsDict.Add(insertedStashRef, options);
            }

            // No need to sort if only have a single tab
            if (_stashTabOptionsDict.Count == 1)
                return true;

            // Assign the new sort order to the tab
            int oldSortOrder = options.SortOrder;
            options.SortOrder = newSortOrder;

            // Rearrange other tabs
            int sortIncrement, sortStart, sortFinish;

            if (oldSortOrder < newSortOrder)
            {
                // If the sort order is increasing we need to shift back everything in between
                sortIncrement = -1;
                sortStart = oldSortOrder;
                sortFinish = newSortOrder;
            }
            else
            {
                // If the sort order is decreasing we need to shift forward everything in between
                sortIncrement = 1;
                sortStart = newSortOrder;
                sortFinish = oldSortOrder;
            }

            // Fall back in case our sort order overflows for some reason
            SortedList<int, PrototypeId> sortedTabs = new();
            bool orderOverflow = false;

            // Update sort order for all tabs
            foreach (var kvp in _stashTabOptionsDict)
            {
                PrototypeId sortRef = kvp.Key;
                StashTabOptions sortOptions = kvp.Value;

                if (sortRef != insertedStashRef)
                {
                    // Move the tab if:
                    // 1. We are adding a new tab and everything needs to be shifted
                    // 2. We are within our sort range
                    bool isNew = oldSortOrder == newSortOrder && sortOptions.SortOrder >= newSortOrder;
                    bool isWithinSortRange = sortOptions.SortOrder >= sortStart && sortOptions.SortOrder <= sortFinish;

                    if (isNew || isWithinSortRange)
                        sortOptions.SortOrder += sortIncrement;
                }

                // Make sure our sort order does not exceed the amount of stored stash tab options
                sortedTabs[sortOptions.SortOrder] = sortRef;
                if (sortOptions.SortOrder >= _stashTabOptionsDict.Count)
                    orderOverflow = true;
            }

            // Reorder if our sort order overflows
            if (orderOverflow)
            {
                Logger.Warn($"StashTabInsert(): Sort order overflow, reordering");
                int fixedOrder = 0;
                foreach (var kvp in sortedTabs)
                {
                    _stashTabOptionsDict[kvp.Value].SortOrder = fixedOrder;
                    fixedOrder++;
                }
            }

            return true;
        }

        /// <summary>
        /// Add the specified badge to this <see cref="Player"/>. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool AddBadge(AvailableBadges badge) => _badges.Add(badge);

        /// <summary>
        /// Removes the specified badge from this <see cref="Player"/>. Returns <see langword="true"/> if successful.
        /// </summary>
        public bool RemoveBadge(AvailableBadges badge) => _badges.Remove(badge);

        /// <summary>
        /// Returns <see langword="true"/> if this <see cref="Player"/> has the specified badge.
        /// </summary>
        public bool HasBadge(AvailableBadges badge) => _badges.Contains(badge);


        #region Hacky Avatar Management

        public void SetAvatar(PrototypeId prototypeId)
        {
            uint librarySlot = 0;

            foreach (Avatar avatar in AvatarList)
            {
                if (avatar.BaseData.PrototypeId == prototypeId)
                {
                    CurrentAvatar = avatar;
                    avatar.BaseData.InvLoc.InventoryRef = (PrototypeId)9555311166682372646;
                    avatar.BaseData.InvLoc.Slot = 0;
                    continue;
                }

                avatar.BaseData.InvLoc.InventoryRef = (PrototypeId)5235960671767829134;
                avatar.BaseData.InvLoc.Slot = librarySlot++;
            }
        }

        #endregion

        protected override void BuildString(StringBuilder sb)
        {
            base.BuildString(sb);

            sb.AppendLine($"{nameof(_missionManager)}: {_missionManager}");
            sb.AppendLine($"{nameof(_avatarProperties)}: {_avatarProperties}");
            sb.AppendLine($"{nameof(_shardId)}: {_shardId}");
            sb.AppendLine($"{nameof(_playerName)}: {_playerName}");
            sb.AppendLine($"{nameof(_consoleAccountIds)}[0]: {_consoleAccountIds[0]}");
            sb.AppendLine($"{nameof(_consoleAccountIds)}[1]: {_consoleAccountIds[1]}");
            sb.AppendLine($"{nameof(_secondaryPlayerName)}: {_secondaryPlayerName}");
            sb.AppendLine($"{nameof(_matchQueueStatus)}: {_matchQueueStatus}");
            sb.AppendLine($"{nameof(_emailVerified)}: {_emailVerified}");
            sb.AppendLine($"{nameof(_accountCreationTimestamp)}: {Clock.UnixTimeToDateTime(_accountCreationTimestamp)}");
            sb.AppendLine($"{nameof(_partyId)}: {_partyId}");

            if (_guildId != GuildMember.InvalidGuildId)
            {
                sb.AppendLine($"{nameof(_guildId)}: {_guildId}");
                sb.AppendLine($"{nameof(_guildName)}: {_guildName}");
                sb.AppendLine($"{nameof(_guildMembership)}: {_guildMembership}");
            }

            sb.AppendLine($"{nameof(_community)}: {_community}");

            for (int i = 0; i < _unlockedInventoryList.Count; i++)
                sb.AppendLine($"{nameof(_unlockedInventoryList)}[{i}]: {GameDatabase.GetPrototypeName(_unlockedInventoryList[i])}");

            if (_badges.Any())
            {
                sb.Append($"{nameof(_badges)}: ");
                foreach (AvailableBadges badge in _badges)
                    sb.Append(badge.ToString()).Append(' ');
                sb.AppendLine();
            }

            sb.AppendLine($"{nameof(_gameplayOptions)}: {_gameplayOptions}");
            sb.AppendLine($"{nameof(_achievementState)}: {_achievementState}");

            foreach (var kvp in _stashTabOptionsDict)
                sb.AppendLine($"{nameof(_stashTabOptionsDict)}[{GameDatabase.GetFormattedPrototypeName(kvp.Key)}]: {kvp.Value}");
        }

        /// <summary>
        /// Initializes <see cref="StashTabOptions"/> for any stash tabs that are unlocked but don't have any options yet.
        /// </summary>
        private void OnEnterGameInitStashTabOptions()
        {
            foreach (PrototypeId stashRef in GetStashInventoryProtoRefs(false, true))
            {
                if (_stashTabOptionsDict.ContainsKey(stashRef) == false)
                    StashTabInsert(stashRef, 0);
            }
        }

        public List<IMessage> OnLoadAndPlayKismetSeq(PlayerConnection playerConnection)
        {
            
            List<IMessage> messageList = new();
            
            if (playerConnection.RegionDataRef != PrototypeId.Invalid)
            {
                KismetSeqPrototypeId kismetSeqRef = 0;
                RegionPrototypeId regionPrototypeId = (RegionPrototypeId)playerConnection.RegionDataRef;
                if (regionPrototypeId == RegionPrototypeId.NPERaftRegion) kismetSeqRef = KismetSeqPrototypeId.RaftHeliPadQuinJetLandingStart;
                if (regionPrototypeId == RegionPrototypeId.TimesSquareTutorialRegion) kismetSeqRef = KismetSeqPrototypeId.Times01CaptainAmericaLanding;
                if (kismetSeqRef != 0)
                    messageList.Add(NetMessagePlayKismetSeq.CreateBuilder().SetKismetSeqPrototypeId((ulong)kismetSeqRef).Build());
            }
            return messageList;
        }

        public Region GetRegion()
        {
            // TODO check work
            if (Game == null) return null;
            var manager = Game.RegionManager;
            if (manager == null) return null;
            return manager.GetRegion(RegionId);
        }

        public void OnPlayKismetSeqDone(PlayerConnection playerConnection, PrototypeId kismetSeqPrototypeId)
        {
            List<IMessage> messages = new();
            if (kismetSeqPrototypeId == PrototypeId.Invalid) return;
            
            if ((KismetSeqPrototypeId)kismetSeqPrototypeId == KismetSeqPrototypeId.RaftHeliPadQuinJetLandingStart)
            {
                // TODO trigger by hotspot
                KismetSeqPrototypeId kismetSeqRef = KismetSeqPrototypeId.RaftHeliPadQuinJetDustoff;
                messages.Add(NetMessagePlayKismetSeq.CreateBuilder().SetKismetSeqPrototypeId((ulong)kismetSeqRef).Build());
                kismetSeqRef = KismetSeqPrototypeId.RaftNPEJuggernautEscape;
                messages.Add(NetMessagePlayKismetSeq.CreateBuilder().SetKismetSeqPrototypeId((ulong)kismetSeqRef).Build());
            }
            if (messages.Count > 0)
                foreach( var message in messages)
                    playerConnection.PostMessage(message);
        }
    }
}
