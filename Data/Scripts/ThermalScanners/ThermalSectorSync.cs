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
				
				// if player has no character or is dead or is not in a cockpit, skip them
				if (localPlayer == null || localPlayer.Character == null || localPlayer.Character.IsDead || localPlayer.Controller?.ControlledEntity == null) {
					return;
				}

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
						createdGps.DiscardAt = MyAPIGateway.Session.ElapsedPlayTime + new TimeSpan(0, 0, 15);
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
			HashSet<String> alreadySeenGrids = new HashSet<String>();
			Dictionary<long, HashSet<String>> seenGridsPerPlayer = new Dictionary<long, HashSet<String>>();
			
            try {

                if (!init) {
					MyLog.Default.WriteLineAndConsole($"[Thermal] (Sector sync) Not initialized, setting up...");
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

					timer = 900; //15 seconds

					//MyLog.Default.WriteLineAndConsole($"[Thermal] Searching for gps... players: {players.Count}");	
                    foreach (IMyPlayer p in players)
                    {
                        // if player has no character or is dead or is not in a cockpit, skip them
						if(p == null || p.Character == null || p.Character.IsDead || 
						   p.Controller?.ControlledEntity == null || p.Controller.ControlledEntity.GetType().Name.ToLower() == "mycharacter") {
							continue;
						}
						
                        // get his GPS list
						List<IMyGps> gpsListTmp = MyAPIGateway.Session.GPS.GetGpsList(p.IdentityId);
						//MyLog.Default.WriteLineAndConsole($"[Thermal] Searching through {gpsListTmp.Count} gps points for player {p.IdentityId}");
						
						GpsCustomType closestGps = null;
						double closestDistance = double.MaxValue;
                        foreach (var gps in gpsListTmp) {
                            // find all GPS positions that fit the criteria
							
							if (gps.Name.Contains("Thermal Signature") && gps.Description.Contains("-")) {
								MyAPIGateway.Session.GPS.RemoveGps(p.IdentityId, gps);
							}

                            if (gps.Name.Contains("Thermal Signature") && (gps.Name.Contains("Synced (TS):") || gps.Description.Contains("-"))){
                                continue;
							}
							
							if (!gps.Name.Contains("Thermal Signature")) {
								continue;
							}
							
							//MyLog.Default.WriteLineAndConsole($"[Thermal] gps.Name: {gps.Name}");
							
							if (!seenGridsPerPlayer.ContainsKey(p.IdentityId)) {
								seenGridsPerPlayer[p.IdentityId] = new HashSet<String>();
							}
							
							seenGridsPerPlayer[p.IdentityId].Add(gps.Description);

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
						
						if (closestGps != null && !alreadySeenGrids.Contains(closestGps.Description)) {
							gpsList.Add(closestGps);
							alreadySeenGrids.Add(closestGps.Description);
						}
                    }
					
					//MyLog.Default.WriteLineAndConsole($"Found {gpsList.Count} thermal gps");
					
					foreach (IMyPlayer p in players) {
						if(p == null || p.Character == null || p.Character.IsDead || 
						   p.Controller?.ControlledEntity == null || p.Controller.ControlledEntity.GetType().Name.ToLower() == "mycharacter") {
							continue;
						}
						
						foreach (GpsCustomType gps in gpsList) {
							if (seenGridsPerPlayer.ContainsKey(p.IdentityId) && seenGridsPerPlayer[p.IdentityId].Contains(gps.Description)) {
								continue;
							}
							
							var start = gps.Name.IndexOf("(") + 1;
							
							var distance = float.Parse(gps.Name.Substring(start, gps.Name.IndexOf(")") - start - 3)) * 1000;

							//MyLog.Default.WriteLineAndConsole($"GPS distance is {distance:0.00}, player distance is {Vector3D.Distance(p.GetPosition(), gps.Coords):0.00}");	

							if (Vector3D.Distance(p.GetPosition(), gps.Coords) <= distance) {										
								var newGps = MyAPIGateway.Session.GPS.Create(gps.Name, gps.Description + "-", gps.Coords, false, true);
                                newGps.DiscardAt = MyAPIGateway.Session.ElapsedPlayTime + new TimeSpan(0, 0, 15);
                                newGps.GPSColor = GetThreat(distance);
                                MyAPIGateway.Session.GPS.AddGps(p.IdentityId, newGps);
								MyAPIGateway.Session.GPS.SetShowOnHud(p.IdentityId, newGps, true);
							}
						}
					}

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
		
		public Color GetThreat(float thermalOutput)
        {
            if (thermalOutput <= 25000)
            {
                return Color.White;
            } else if (thermalOutput <= 50000)
            {
                return Color.LightBlue;
            } else if (thermalOutput <= 100000)
            {
                return Color.Yellow;
            } else if (thermalOutput <= 150000)
            {
                return Color.Orange;
            } else
            {
                return Color.Red;
            }

        }
	}
}
