using System;
using System.Collections.Generic;
using System.Linq;
using Sandbox.Definitions;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character.Components;
using Sandbox.ModAPI;
using VRage.Collections;
using VRage.Game;
using VRage.Game.Components;
using VRage.Game.ModAPI;
using VRage.Game.ModAPI.Interfaces;
using VRage.Game.Entity;
using VRage.ModAPI;
using VRage.ObjectBuilders;
using VRage.Utils;
using VRageMath;
using NexusAPIns;
using ThermalSectorSync.Descriptions;

namespace ThermalSectorSync.Session
{

	[MySessionComponentDescriptor(MyUpdateOrder.AfterSimulation)]

	public class ThermalSectorSync : MySessionComponentBase
	{

        bool isServer = false;
        bool init = false;
        public bool nexusInit = false;
        public NexusAPI Nexus;
        public const ushort CliComId = 42699;
        public int timer = 0;

        public override void UpdateAfterSimulation()
        {
            Update();
        }

		protected override void UnloadData()
		{
			Unload();
		}

        private void Init()
		{
			init = true;

			isServer = MyAPIGateway.Session.OnlineMode == MyOnlineModeEnum.OFFLINE || MyAPIGateway.Multiplayer.IsServer;

			try {
				if (isServer) {
					// nexus init + handle cross server GPS broadcast
					if (!nexusInit) {
						Nexus = new NexusAPI(5440);
						MyAPIGateway.Multiplayer.RegisterMessageHandler(5440, HandleCrossServerThermalSignature);
						nexusInit = true;
					}
				}

				if (!isServer)
				{
					// this is where clients get their GPS
					MyAPIGateway.Multiplayer.RegisterMessageHandler(CliComId, HandleCrossServerClientThermalSignature);
				}
			} catch (Exception e) {
				MyLog.Default.WriteLineAndConsole($"[Thermal] sync error, ERROR: {e}");
			}

		}

        private void HandleCrossServerThermalSignature(byte[] obj)
        {
            try {
                if (obj == null)
                    return;

                MyAPIGateway.Multiplayer.SendMessageToOthers(CliComId, obj, true);

            } catch(Exception exc) {
                MyLog.Default.WriteLineAndConsole($"[Thermal] sync error, ERROR: {exc}");
            }
        }

        private void HandleCrossServerClientThermalSignature(byte[] obj)
        {
            try {
                if (obj == null)
                    return;

                List<GpsCustomType> gpsList = MyAPIGateway.Utilities.SerializeFromBinary<List<GpsCustomType>>(obj);

                IMyPlayer localPlayer = MyAPIGateway.Session.LocalHumanPlayer;

                if (localPlayer == null)
                    return;

                foreach (GpsCustomType gps in gpsList)
                {
                    if (gps == null)
                        continue;
					
					var start = gps.Name.IndexOf("(") + 1;
					var distance = float.Parse(gps.Name.Substring(start, gps.Name.IndexOf(")") - start - 3)) * 1000;

					//MyLog.Default.WriteLineAndConsole($"[Thermal] creating synced gps: {gps.Name}");					

					if (Vector3D.Distance(localPlayer.GetPosition(), gps.Coords) <= distance) {		
						//MyLog.Default.WriteLineAndConsole($"[Thermal] creating synced gps: {gps.Name}");
						var syncedGpsName = "Synced (TS): " + gps.Name;
						var createdGps = MyAPIGateway.Session.GPS.Create(syncedGpsName, gps.Description, gps.Coords, true, true);
						createdGps.DiscardAt = MyAPIGateway.Session.ElapsedPlayTime + new TimeSpan(0, 0, 30);
						createdGps.GPSColor = new Color(255,255,153);
						MyAPIGateway.Session.GPS.AddLocalGps(createdGps);
					}
                }

            } catch(Exception exc) {
                MyLog.Default.WriteLineAndConsole($"[Thermal] Sync error, 	 {exc}");
            }
        }

        private void Update()
        {
            try {

                if (!init) {
					MyLog.Default.WriteLineAndConsole($"[Thermal] Not initialized, setting up...");
                    if (MyAPIGateway.Session == null) {
						//MyLog.Default.WriteLineAndConsole($"[Thermal] session null");
                        return;
					}
                    Init();
                }
				
				if (!isServer) {
					return;
				}

                timer--;
                if (timer < 1) {
                    // get all players
                    List<IMyPlayer> players = new List<IMyPlayer>();
                    MyAPIGateway.Players.GetPlayers(players);

                    List<GpsCustomType> gpsList = new List<GpsCustomType>();

					timer = 1800; //30 seconds
					long timestamp = (int)DateTime.UtcNow.Subtract(new DateTime(1970, 1, 1)).TotalSeconds * (long)1000;

					//MyLog.Default.WriteLineAndConsole($"[Thermal] Searching for gps... players: {players.Count}");
					
					GpsCustomType closestGps = null;
					double closestDistance = double.MaxValue;
					
                    foreach (IMyPlayer p in players)
                    {
                        // if player has no character or is dead or is not in a cockpit, skip them
                        if (p.Character == null || p.Character.IsDead || p.Controller?.ControlledEntity == null) {
                            continue;
						}
						if (p.Controller.ControlledEntity.GetType().Name.ToLower() != "mycharacter") {
							continue;
						}

                        // get his GPS list
						List<IMyGps> gpsListTmp = MyAPIGateway.Session.GPS.GetGpsList(p.IdentityId);
						//MyLog.Default.WriteLineAndConsole($"[Thermal] Searching through {gpsListTmp.Count} gps points for player {p.IdentityId}");
                        foreach (var gps in gpsListTmp) {
                            // find all GPS positions that fit the criteria

                            if (!gps.Name.Contains("Thermal Signature") || gps.Name.Contains("Synced (TS):")) {
                                continue;
							}
							
							//MyLog.Default.WriteLineAndConsole($"[Thermal] gps.Name: {gps.Name}");

							GpsCustomType customGps = new GpsCustomType() {
								Name = gps.Name,
								Description = gps.Description,
								Coords = gps.Coords
							};
							
							double distance = Vector3D.Distance(p.GetPosition(), gps.Coords);
							if (distance < closestDistance) {
								closestDistance = distance;
								closestGps = customGps;
							}
                        }
                    }
					
					if (closestGps != null) {
						gpsList.Add(closestGps);
					}

                    // broadcast closest gps to all other sectors
                    if (gpsList.Count > 0) {
						//MyLog.Default.WriteLineAndConsole($"[Thermal] sending gps to other servers...");
                        var serializedGps = MyAPIGateway.Utilities.SerializeToBinary<List<GpsCustomType>>(gpsList);
                        Nexus.SendMessageToAllServers(serializedGps);
                    }
                }

            } catch(Exception exc) {
                MyLog.Default.WriteLineAndConsole($"[Thermal] sync error, ERROR: {exc}");
            }
        }

		private void Unload()
		{
            if (isServer) {
                if (nexusInit) {
                    MyAPIGateway.Multiplayer.UnregisterMessageHandler(5440, HandleCrossServerThermalSignature);
                }
            } else {
                MyAPIGateway.Multiplayer.UnregisterMessageHandler(CliComId, HandleCrossServerClientThermalSignature);
            }
		}
	}
}
