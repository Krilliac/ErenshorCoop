namespace ErenshorDedicatedServer.Data
{
    /// <summary>
    /// Packet type identifiers. Must match client-side PacketType enum exactly.
    /// </summary>
    public enum PacketType : byte
    {
        DONT_RESEND = 0,
        SERVER_CONNECT = 1,
        SERVER_INFO = 2,
        DISCONNECT = 3,
        PLAYER_CONNECT = 4,
        PLAYER_DATA = 5,
        PLAYER_TRANSFORM = 6,
        PLAYER_ACTION = 7,
        ENTITY_DATA = 8,
        ENTITY_SPAWN = 9,
        ENTITY_TRANSFORM = 10,
        ENTITY_ACTION = 11,
        PLAYER_MESSAGE = 12,
        PLAYER_REQUEST = 13,
        SERVER_REQUEST = 14,
        GROUP = 15,
        SERVER_GROUP = 16,
        ITEM_DROP = 17,
        WEATHER_DATA = 18
    }

    public enum EntityType : byte
    {
        ENEMY = 0,
        SIM = 1,
        PET = 2,
        PLAYER = 3,
        LOCAL_PLAYER = 4,
    }

    public enum PlayerDataType : byte
    {
        POSITION = 0,
        ROTATION = 1,
        GEAR = 2,
        HEALTH = 3,
        SCENE = 4,
        NAME = 5,
        ANIM = 6,
        LEVEL = 7,
        CLASS = 8,
        MP = 9,
        CURTARGET = 10,
        DESTR_SIM = 11,
        PERIODIC_UPDATE = 12,
        STATS = 13,
        RENAME = 14
    }

    public enum EntityDataType : byte
    {
        POSITION = 0,
        ROTATION = 1,
        ANIM = 2,
        HEALTH = 3,
        SIM_REMOVE = 4,
        ENTITY_REMOVE = 5,
        MP = 6,
        CURTARGET = 7,
        PERIODIC_UPDATE = 8
    }

    public enum ActionType : byte
    {
        ATTACK = 0,
        DAMAGE_TAKEN = 1,
        SPELL_CHARGE = 2,
        SPELL_EFFECT = 3,
        SPELL_END = 4,
        REVIVE = 5,
        STATUS_EFFECT_APPLY = 6,
        STATUS_EFFECT_REMOVE = 7,
        HEAL = 8,
        WAND_ATTACK = 9,
        WORN_EFFECT_REFRESH = 10,
        ACTIVE_STATUS_EFFECTS = 11
    }

    public enum AnimatorSyncType : byte
    {
        BOOL = 0,
        FLOAT = 1,
        INT = 2,
        TRIG = 3,
        RSTTRIG = 4,
        OVERRIDE = 5
    }

    public enum ServerInfoType : byte
    {
        WEATHER = 0,
        PVP_MODE = 1,
        ZONE_OWNERSHIP = 2,
        SERVER_SETTINGS = 3,
        HOST_MODS = 4,
        PLAYER_LIST = 5,
    }

    public enum MessageType : byte
    {
        SAY = 0,
        GROUP = 1,
        SHOUT = 2,
        WHISPER = 3,
        INFO = 4,
        BATTLE_LOG = 5,
    }

    public enum ItemDropType : byte
    {
        DROP = 0,
        DESTROY = 1,
        NEW_QUANTITY = 2
    }

    public enum GroupDataType : byte
    {
        INVITE = 0,
        INVITE_RESPONSE = 1,
        ACCEPT_DECLINE = 2,
        MEMBER_LIST = 3,
        REMOVE = 4,
        INVITE_SIM = 5,
        EXPERIENCE = 6,
        SIM_FOLLOW = 7
    }

    public enum RequestType : byte
    {
        ENTITY_ID = 0,
        MOD_COMMAND = 1,
        ENTITY_SPAWN = 2,
    }

    public enum CustomSpawnID
    {
        MALAROTH = -1,
        CHESS = -2,
        SIRAETHE = -3,
        ADDS = -4,
        TREASURE_GUARD = -5,
        ASTRA = -6,
        WAVE_EVENT = -7,
        SPAWN_TRIGGER = -8,
        FERNALLA_WARD = -9,
        FERNALLA_PORTAL = -10,
        PRE_SYNCED = -11,
    }

    public enum DamageType : byte
    {
        Physical = 0,
        Magic = 1,
        Fire = 2,
        Cold = 3,
        Poison = 4,
        Disease = 5,
    }

    public enum PlayerClass : byte
    {
        Paladin = 0,
        Druid = 1,
        Duelist = 2,
        Arcanist = 3,
        Stormcaller = 4,
        Unknown = 255,
    }

    public enum NpcState : byte
    {
        Idle,
        Wandering,
        Chasing,
        Attacking,
        Returning,
        Dead,
        Spawning,
    }
}
