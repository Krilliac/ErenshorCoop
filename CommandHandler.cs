using ErenshorCoop.Client;
using ErenshorCoop.Server;
using ErenshorCoop.Shared;
using ErenshorCoop.Shared.Packets;
using HarmonyLib;
using System.Net;
using System.Text.RegularExpressions;
using UnityEngine;


namespace ErenshorCoop
{
	public class CommandHandler
	{
		public static void CreateHooks(Harmony harm)
		{
			ErenshorCoopMod.CreatePrefixHook(typeof(TypeText), "CheckCommands", typeof(CommandHandler), "CheckCommands_Prefix");
		}

		public static bool CheckCommands_Prefix(TypeText __instance)
		{
			string txt = __instance.typed.text;

			if (string.IsNullOrEmpty(txt)) return true;

			if (txt.StartsWith("/"))
			{
				//Split
				string[] spl = txt.Substring(1).Split(' ');
				string command = spl[0].ToLower();

				switch (command)
				{
#if DEBUG
					case "warpto":
						foreach(var p in ClientConnectionManager.Instance.Players)
						{
							if(p.Value.name == "Valk")
							{
								GameData.PlayerControl.transform.GetComponent<CharacterController>().enabled = false;
								ClientConnectionManager.Instance.LocalPlayer.transform.position = p.Value.transform.position;
									Logging.Log($"dbg warped");
								GameData.PlayerControl.transform.GetComponent<CharacterController>().enabled = true;
								break;
							}
						}
						break;
#endif
					case "kick":
					case "ban":
						return HandleModCommand(command, spl);
				}
			}
			return true;
		}

		private static bool HandleModCommand(string command, string[] args)
		{
			if (!ClientConnectionManager.Instance.IsRunning)
				return true; // Not connected, let game handle it

			if (!Steam.Lobby.isInLobby)
			{
				Logging.WriteInfoMessage("Moderator commands are only supported using steam lobbies.");
				return false;
			}

			if (args.Length != 2)
			{
				Logging.WriteInfoMessage($"Usage: /{command} <name>");
				return false;
			}

			var targetName = args[1].ToLower();
			if (targetName == GameData.CurrentCharacterSlot.CharName.ToLower())
			{
				Logging.WriteInfoMessage($"Cannot {command} self!");
				return false;
			}

			byte commandType = (byte)(command == "kick" ? 0 : 1);

			if (ServerConnectionManager.Instance.IsRunning)
			{
				ClientConnectionManager.Instance.HandleModCommand(commandType, targetName);
			}
			else
			{
				var pa = PacketManager.GetOrCreatePacket<PlayerRequestPacket>(ClientConnectionManager.Instance.LocalPlayerID, PacketType.PLAYER_REQUEST);
				pa.dataTypes.Add(Request.MOD_COMMAND);
				pa.playerName = targetName;
				pa.commandType = commandType;
			}

			return false;
		}
	}
}
