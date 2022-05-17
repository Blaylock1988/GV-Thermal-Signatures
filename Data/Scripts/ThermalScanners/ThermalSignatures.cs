﻿
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Character;
using Sandbox.ModAPI;
using SpaceEngineers.Game.ModAPI;
using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using VRage.Game.Components;
using VRage.Game.Entity;
using VRage.Game.ModAPI;
using VRage.ModAPI;
using VRage.Utils;
using VRageMath;
using Jakaria.API;
using IMyCubeGrid = VRage.Game.ModAPI.IMyCubeGrid;
using IMyEntity = VRage.ModAPI.IMyEntity;
using IMyWeatherEffects = VRage.Game.ModAPI.IMyWeatherEffects;
using Sandbox.Game.SessionComponents;


namespace ThermalScanners
{

    [MySessionComponentDescriptor(MyUpdateOrder.BeforeSimulation)]

    public class ThermalSignatures : MySessionComponentBase {
        public string ReleaseVersion = "0.0.1";

        //Server
        public bool IsServer = false;
        public bool IsDedicated = false;
        public int ticks = 0;
        public Guid modStorageGuid = Guid.Parse("58e1fd1d-015f-4aa5-b300-036f4342e2a8");

        public static Dictionary<IMyCubeGrid, HeatMaps> heatMaps;
        
        public List<IMyCubeGrid> scannedGrids;

        public Dictionary<string, float> ThermalSignaturesHistory;

        public readonly List<MyPlanet> planets = new List<MyPlanet>();

        //grid holdings
        public HashSet<IMyEntity> myCubes;


        //Setup variables
        public bool FinishedSetup = false;
		
		//Nebula stuff (borrowed from MES)
		public float nebulaEffect;
		public bool insideNebula;
		public float nebulaDensity;
		public float nebulaMaterial;
		public string nebulaWeather;

        public override void UpdateBeforeSimulation()
        {
			try {
				Update();
			}
			catch (Exception exc) {
				MyLog.Default.WriteLineAndConsole($"[Thermal] Signature error, 	 {exc}");
			}
		}
		
		public void Update() {
            if (FinishedSetup == false)
            {
                //MyLog.Default.WriteLineAndConsole("Starting Setup");
                FinishedSetup = true;
                Setup();
            }
            if (!IsServer || (IsServer && !IsDedicated) )
            {
              
                ticks++;

                if (ticks % 900 == 0)
                {
                    if (ThermalSync.HeatGenerators == null || ThermalSync.HeatGenerators.Count == 0)
                    {
                        MyLog.Default.WriteLineAndConsole("Waiting for server to send the list for Heat Generators");
                        ThermalSync.RequestUpdate(MyAPIGateway.Session.LocalHumanPlayer.IdentityId);
                        return;
                    }

                    if (ThermalSync.HeatSettings == null)
                    {
                        MyLog.Default.WriteLineAndConsole("Waiting for server to send the list for the Heat Settings");
                        ThermalSync.RequestUpdate(MyAPIGateway.Session.LocalHumanPlayer.IdentityId);
                        return;
                    }

                    ticks = 0;
                    scannedGrids = new List<IMyCubeGrid>();
                    heatMaps = new Dictionary<IMyCubeGrid, HeatMaps>();
					if (MyAPIGateway.Session == null) {
						return;
					}
					
                    var player = MyAPIGateway.Session.LocalHumanPlayer;
					
					if (player == null) {
						return;
					}
					
					foreach (var oldGps in MyAPIGateway.Session.GPS.GetGpsList(player.IdentityId)) {
						if (oldGps.Name.Contains("Thermal Signature")) {
							
							if (!oldGps.Name.Contains("Synced")) {
								MyAPIGateway.Session.GPS.RemoveGps(player.IdentityId, oldGps);
							}
						}
					}
					
                    if (MyAPIGateway.Session.IsCameraControlledObject && !player.Character.IsDead && player.Controller.ControlledEntity.GetType().Name.ToLower() != "mycharacter")
                    {
						if (myCubes.Count == 0) {
							var sphere = new BoundingSphereD(player.GetPosition(), 30000);
							HashSet<IMyEntity> entities = new HashSet<IMyEntity>(MyAPIGateway.Entities.GetEntitiesInSphere(ref sphere));

							foreach (IMyEntity e in entities) {
								if (!(e is IMyCubeGrid)) {
									continue;
								}
					
								if ((e as IMyCubeGrid).GridSizeEnum == VRage.Game.MyCubeSize.Small ||
									(e as IMyCubeGrid).GridSizeEnum == VRage.Game.MyCubeSize.Large) {
									var grid = e as IMyCubeGrid;
									List<IMyCubeGrid> grids = new List<IMyCubeGrid>();
									var gridGroup = grid.GetGridGroup(GridLinkTypeEnum.Physical);
									gridGroup.GetGrids(grids);
									
									bool isStatic = false;
									foreach (IMyCubeGrid g in grids) {
										if (g.IsStatic) {
											isStatic = true;
											break;
										}
										
										if (!isStatic && ((MyCubeGrid) grid).BlocksCount > ((MyCubeGrid) g).BlocksCount) {
											grid = g;
										}
									}
									
									if (!isStatic) {
										myCubes.Add(grid);
									}
								}
							}
						}

                        //MyLog.Default.WriteLineAndConsole("Scanning");
                        foreach (var grid in myCubes)
                        {
                            if (grid == null || !(grid is MyCubeGrid))
                            {
                                continue;
                            }

                            var thermalOutput = GetThermalOutput(grid as IMyCubeGrid);

                            //MyLog.Default.WriteLineAndConsole("Checking Storage");
                            

							var asGrid = grid as IMyCubeGrid;
							var gridId = asGrid.EntityId.ToString();
                            IMyCubeGrid sameAs = null;
                            var testConnected = scannedGrids.Find(gridSearch =>
                            {
                                if (asGrid.IsSameConstructAs(gridSearch))
                                {
                                    sameAs = gridSearch;
                                    return true;
                                }
                                return false;
                            });

                            if (testConnected == null)
                            {
                                scannedGrids.Add(asGrid);
                            }

                            if (!ThermalSignaturesHistory.ContainsKey(gridId))
                            {
                                //grid.Storage.Add(new KeyValuePair<Guid, string>(modStorageGuid, thermalOutput.ToString()));
                                ThermalSignaturesHistory.Add(gridId, thermalOutput);
                                //MyLog.Default.WriteLineAndConsole("New Storage");
                            }
                            else
                            {
                                //MyLog.Default.WriteLineAndConsole("Old Storage");
                                //there is a thermal Decay. You can't just shut down and magically cause your signature to disappear.
                                float oldThermal = ThermalSignaturesHistory[gridId];
                                //MyLog.Default.WriteLineAndConsole("Parsed old:" + oldThermal.ToString());
                                if (oldThermal >= thermalOutput)
                                {
									//Average of prev heat and current heat, but lose 10km at the worst so heat eventually disappates
									//and doesn't just halve forever
									var newThermal = (thermalOutput + oldThermal) / 2;
									newThermal = Math.Min(newThermal, oldThermal - 10000);
                                    thermalOutput = Math.Max(thermalOutput, newThermal);
									
                                    //grid.Storage.SetValue(modStorageGuid, thermalOutput.ToString());
                                    ThermalSignaturesHistory[gridId] = thermalOutput;
                                }
                                else
                                {
                                    ThermalSignaturesHistory[gridId] = thermalOutput;
                                }

                            }

                            //

                            //MyLog.Default.WriteLineAndConsole($"{grid.EntityId}:{thermalOutput}");
                            var location = grid.GetPosition();

                            if (testConnected == null)
                            {
                                var heatmap = new HeatMaps
                                {
                                    Heat = thermalOutput,
                                    Location = location
                                };
                                heatMaps.Add(asGrid, heatmap);
                            }
                            else
                            {
                                var heatMap = heatMaps[sameAs];
                                heatMap.Heat += thermalOutput;
                                heatMap.Location = Vector3.Divide(heatMap.Location + sameAs.GetPosition(), 2);
                                heatMaps[sameAs] = heatMap;
                            }

                        }

                        foreach (var grid in heatMaps)
                        {
                            var location = grid.Value.Location;
                            var thermalOutput = grid.Value.Heat;
                            if (Vector3D.Distance(player.GetPosition(), location) <= thermalOutput && thermalOutput > 15000)
                            {
								
								var distanceInKm = Convert.ToInt32(thermalOutput) / 1000;

                                var gps = MyAPIGateway.Session.GPS.Create($"Thermal Signature ({distanceInKm} km)", $"GetTimeMs()", location, false, true);
                                gps.DiscardAt = MyAPIGateway.Session.ElapsedPlayTime + new TimeSpan(0, 0, 12);
                                gps.GPSColor = GetThreat(thermalOutput);
                                MyAPIGateway.Session.GPS.AddGps(player.IdentityId, gps);
								MyAPIGateway.Session.GPS.SetShowOnHud(player.IdentityId, gps, true);
                            }
                        }

                    }
                }
            }
        }
		
		private long GetTimeMs() {
            return (long)(DateTime.Now.ToUniversalTime() - new DateTime(1970, 1, 1)).TotalMilliseconds;
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

        public void GetPlanets()
        {
            MyAPIGateway.Entities.GetEntities(null, entity =>
            {
                var planet = entity as MyPlanet;

                if (planet != null)
                {
                    planets.Add(planet);
                }
                return false;
            });
        }
        //MyAPIGateway.Multiplayer.UnregisterMessageHandler(Sync.UModId, Sync.Recieve);
        public void Setup()
        {
            IsServer = MyAPIGateway.Multiplayer.IsServer;
            IsDedicated = MyAPIGateway.Utilities.IsDedicated;
            GetHeatGeneratorsAndDefaultSettings();

            if (!IsDedicated)
            {
                MyLog.Default.WriteLineAndConsole($"Not Dedicated");
                GetPlanets();
                MyLog.Default.WriteLineAndConsole($"Got Planets");
                if (myCubes == null)
                {
                    MyLog.Default.WriteLineAndConsole($"Set Cubes");
                    myCubes = new HashSet<IMyEntity>();
                    
                }
                if (ThermalSignaturesHistory == null)
                {
                    MyLog.Default.WriteLineAndConsole($"Set History");
                    ThermalSignaturesHistory = new Dictionary<string, float>();
                }

                ThermalSignaturesHistory = new Dictionary<string, float>();

                      MyAPIGateway.Entities.GetEntities(myCubes, grid =>
                            { 
                                if (grid is IMyCubeGrid)
                                {
                                    if ((grid as IMyCubeGrid).GridSizeEnum == VRage.Game.MyCubeSize.Small || (grid as IMyCubeGrid).GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                    {
                                        return true;
                                    }
                                }
                                return false;
                            });
                MyLog.Default.WriteLineAndConsole($"Setting up handlers");
                MyAPIGateway.Entities.OnEntityAdd += Entities_OnEntityAdd;
                ThermalSync.Register();
            }

            if (IsDedicated || IsServer)
            {
                MyLog.Default.WriteLineAndConsole($"Registering Server");
                ThermalSync.RegisterServer();
            }
        }




        private void Bag_OnStaticChanged(MyCubeGrid grid, bool changed)
        {
            if (grid is IMyCubeGrid)
            {
                var bag = grid as IMyCubeGrid;
                if (!bag.IsStatic)
                {
                    if (!myCubes.Contains(grid))
                    {
                        myCubes.Add(grid);
                    }
                }
            }
        }


        private void Entities_OnEntityAdd(IMyEntity obj)
        {
            if (!myCubes.Contains(obj))
            {
                if (obj is IMyCubeGrid)
                {
                    var bag = obj as IMyCubeGrid;
                    if (!bag.IsStatic)
                    {
                        //MyLog.Default.WriteLineAndConsole("Adding Grid");
                        //if we want to split this up down the road, we can do a simple check on the gridsize enum.
                        myCubes.Add(obj);
                    } else
                    {
                        obj.OnPhysicsChanged += Obj_OnPhysicsChanged; ;
                    }
                }
            }
        }

        private void Obj_OnPhysicsChanged(IMyEntity grid)
        {
            if (grid is IMyCubeGrid)
            {
                var bag = grid as IMyCubeGrid;
                if (!bag.IsStatic)
                {
                    if (!myCubes.Contains(grid))
                    {
                        myCubes.Add(grid);
                    }
                }
            }
        }

        private float GetThermalOutput(IMyCubeGrid block)
        {	
            if (block != null)
            {
                var thermalOutput = 0.0f;

                //MyLog.Default.WriteLineAndConsole("Thermal Check");

                if (block == null)
                {
                    //MyLog.Default.WriteLineAndConsole("Block was null");
                    return 0.0f;
                }

				bool isLargeGrid = (block.GridSizeEnum == VRage.Game.MyCubeSize.Large);
                List<IMyPowerProducer> PowerProducers = new List<IMyPowerProducer>();
                List<IMyEntity> ThermalProducers = new List<IMyEntity>();



                if (block.IsStatic)
                {
                    //MyLog.Default.WriteLineAndConsole("Static");
                    return 0.0f;
                }

                var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(block);
                if (gts != null)
                {
                    gts.GetBlocksOfType(PowerProducers, powerBlock =>
                    {
                        if (powerBlock.CubeGrid.EntityId == block.EntityId)
                            return true;
                        else
                            return false;
                    });
                } else
                {
                    //MyLog.Default.WriteLineAndConsole("GTS was null");
                    return 0.0f;
                }

                gts.GetBlocksOfType(ThermalProducers, thermalBlock =>
                {
                    if (thermalBlock is IMyPowerProducer)
                    {
                        return false;
                    }
                    else
                    {
                        if (((IMyTerminalBlock)thermalBlock).CubeGrid.EntityId != block.EntityId)
                            return false;
                        else
                            return true;
                    }
                });

                if (PowerProducers.Count != 0)
                {
                    foreach (var powerProducer in PowerProducers)
                    {
						//normal large reactor is 300mw
						float baseOutput = 300.0f;
						if (!isLargeGrid) {
							baseOutput = 29.5f;
						}
                        if (powerProducer is IMyReactor)
                        {	
                            if (ThermalSync.reactorValue != null && powerProducer.MaxOutput != 0)
                            {
                                var ratio = powerProducer.CurrentOutput / baseOutput;

                                var thermal = ThermalSync.reactorValue.HeatOutput * ratio;
                                thermal = PlanetEffects(thermal, ThermalSync.reactorValue.AirMultiplier, block.GetPosition());
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                {
                                    thermal *= ThermalSync.reactorValue.SmallGridMultiplier;
                                }
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                {
                                    thermal *= ThermalSync.reactorValue.LargeGridMultiplier;
                                }

                                thermalOutput += thermal;
                            }
                            else
                            {
                                thermalOutput += powerProducer.CurrentOutput;
                            }
                        }
                        else if (powerProducer is IMyBatteryBlock)
                        {
                            if (ThermalSync.batteryValue != null && powerProducer.MaxOutput != 0)
                            {
                                var ratio = powerProducer.CurrentOutput / baseOutput;

                                var thermal = ThermalSync.batteryValue.HeatOutput * ratio;
                                thermal = PlanetEffects(thermal, ThermalSync.batteryValue.AirMultiplier, block.GetPosition());

                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                {
                                    thermal *= ThermalSync.batteryValue.SmallGridMultiplier;
                                }
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                {
                                    thermal *= ThermalSync.batteryValue.LargeGridMultiplier;
                                }

                                thermalOutput += thermal;
                            }
                            else
                            {
                                thermalOutput += powerProducer.CurrentOutput * 0.25f;
                            }
                        }
                        else if (powerProducer is IMySolarPanel)
                        {
                            if (ThermalSync.solarValue != null && powerProducer.MaxOutput != 0)
                            {
                                var ratio = powerProducer.CurrentOutput / powerProducer.MaxOutput;

                                var thermal = ThermalSync.solarValue.HeatOutput * ratio;
                                thermal = PlanetEffects(thermal, ThermalSync.solarValue.AirMultiplier, block.GetPosition());
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                {
                                    thermal *= ThermalSync.solarValue.SmallGridMultiplier;
                                }
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                {
                                    thermal *= ThermalSync.solarValue.LargeGridMultiplier;
                                }

                                thermalOutput += thermal;
                            }
                            else
                            {
                                thermalOutput += 0;
                            }
                        }
                        else if (powerProducer.BlockDefinition.SubtypeId.ToLower().Contains("engine")) //because thank you, Keen.
                        {
                            if (ThermalSync.hydrogenValue != null && powerProducer.MaxOutput != 0)
                            {

                                var ratio = powerProducer.CurrentOutput / powerProducer.MaxOutput;

                                var thermal = ThermalSync.hydrogenValue.HeatOutput * ratio;
                                thermal = PlanetEffects(thermal, ThermalSync.hydrogenValue.AirMultiplier, block.GetPosition());
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                {
                                    thermal *= ThermalSync.hydrogenValue.SmallGridMultiplier;
                                }
                                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                {
                                    thermal *= ThermalSync.hydrogenValue.LargeGridMultiplier;
                                }

                                thermalOutput += thermal;
                            }
                            else
                            {
                                thermalOutput += powerProducer.CurrentOutput * 0.5f;
                            }
                        }
                        else  //Wind?
                        {
                            thermalOutput += 0;
                        }
                    }
                }

                if (ThermalProducers.Count != 0)
                {
					//large hydrogen thruster is 7200kn
					float baseThrust = 7200000;
					if (!isLargeGrid) {
						baseThrust = 480000;
					}
                    foreach (var thermalProducer in ThermalProducers)
                    {

                        //thruster logic

                        if (thermalProducer is IMyThrust)
                        {
                            var thrust = thermalProducer as IMyThrust;
                            var subtypeId = thrust.BlockDefinition.SubtypeId.ToLower();

                            foreach (var heat in ThermalSync.thrusterValues)
                            {
                                if (subtypeId.Contains(heat.SubtypeId.ToLower()))
                                {
                                    var ratio = thrust.CurrentThrust / baseThrust;
                                    var thermal = heat.HeatOutput * ratio;
                                    thermal = PlanetEffects(thermal, heat.AirMultiplier, block.GetPosition());
                                    if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                    {
                                        thermal *= heat.SmallGridMultiplier;
                                    }
                                    if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                    {
                                        thermal *= heat.LargeGridMultiplier;
                                    }
                                    thermalOutput += thermal;
                                    break;
                                }
                            }


                        }
                        else
                        {
                            if (thermalProducer is IMyTerminalBlock)
                            {
                                var terminal = thermalProducer as IMyTerminalBlock;
                                var subtypeId = terminal.BlockDefinition.SubtypeId.ToLower();

                                foreach (var heat in ThermalSync.otherBlocks)
                                {
                                    if (subtypeId.Contains(heat.SubtypeId.ToLower()))
                                    {
                                        if (terminal.IsWorking)
                                        {
                                            var thermal = heat.HeatOutput;
                                            thermal = PlanetEffects(thermal, heat.AirMultiplier, block.GetPosition());
                                            if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                                            {
                                                thermal *= heat.SmallGridMultiplier;
                                            }
                                            if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                                            {
                                                thermal *= heat.LargeGridMultiplier;
                                            }
                                            thermalOutput += thermal;
                                        }
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }


                if (block.GridSizeEnum == VRage.Game.MyCubeSize.Large)
                {

                    thermalOutput *= ThermalSync.HeatSettings.LargeGridBaseRange;

                    return thermalOutput;
                } else if (block.GridSizeEnum == VRage.Game.MyCubeSize.Small)
                {
                    thermalOutput *= ThermalSync.HeatSettings.SmallGridBaseRange;
                    return thermalOutput;
                } else
                {
                    return 0.0f;
                }
            } else
            {
                //MyLog.Default.WriteLineAndConsole("Was Null or Not Grid");
                return 0;
            }
        }

        public float PlanetEffects(float thermal, float atmosphere, Vector3D gridPosition)
        {
            var planetEffect = 0.0f;
			var weatherEffect = 0.0f;

            foreach (var planet in planets)
            {

                if (Vector3D.DistanceSquared(gridPosition, planet.WorldMatrix.Translation) < (planet.AtmosphereRadius * planet.AtmosphereRadius))
                {
                    if (atmosphere != 0)
                    {
                        var airDensity = planet.GetAirDensity(gridPosition);
                        if (airDensity > 1)
                        {
                            airDensity = 1;
                        }

                        if (airDensity != 0)
                        {
                            planetEffect += (atmosphere * airDensity) * ThermalSync.HeatSettings.AtmosphericDensity;
                        }
                        
						var weatherIntensity = MyAPIGateway.Session.WeatherEffects.GetWeatherIntensity(gridPosition);
						if (weatherIntensity != null) {
							if (weatherIntensity > 1)
							{
								weatherIntensity = 1;
							}

							//weather should reduce thermal when it exists
							//not sure if clear weather also affects this
							weatherEffect += weatherIntensity * ThermalSync.HeatSettings.WeatherMultiplier;
						}
                    }
                }
            }

			//Compatibility for Jakaria's Nebula mod, borrowed from MES
            nebulaEffect = 0.0f;

			//if (NebulaAPI.Registered) {
				
				if (NebulaAPI.InsideNebula(gridPosition)) {
					
					insideNebula = true;
					nebulaDensity = NebulaAPI.GetNebulaDensity(gridPosition); //nebula densities are often very low, probably not worth using this
					nebulaMaterial = NebulaAPI.GetMaterial(gridPosition); //no use for this at the current time
					nebulaWeather = NebulaAPI.GetWeather(gridPosition) ?? "N/A"; //no way to see weather intensity

					//Also checks for nebula weather effects
					if (nebulaWeather != "N/A") {
						
						nebulaEffect += ThermalSync.HeatSettings.NebulaMultiplier * ThermalSync.HeatSettings.WeatherMultiplier;
						
					} else {

					nebulaEffect += ThermalSync.HeatSettings.NebulaMultiplier;
					
					}
					
				} else {

					insideNebula = false;
					nebulaDensity = 0;
					nebulaMaterial = 0;
					nebulaWeather = "N/A";
				}					
				
			//}
			
			return thermal * (1 - planetEffect) * (1 - weatherEffect) * (1 - nebulaEffect);

        }

        public struct HeatMaps
        {
            public Vector3 Location;
            public float Heat;
        }

        public void GetHeatGeneratorsAndDefaultSettings()
        {
            if (IsDedicated || (IsServer))
            {
                var defaultfile = "DefaultGenerators.xml";

                //ThermalSync.HeatSettings
				/*
                if (MyAPIGateway.Utilities.FileExistsInLocalStorage(defaultfile, typeof(GlobalHeatSettings)))
                {
                    MyLog.Default.WriteLineAndConsole($"Loading Global Heat Generators from DefaultGenerators.xml");
                    using (var buffer = MyAPIGateway.Utilities.ReadFileInLocalStorage(defaultfile, typeof(GlobalHeatSettings)))
                    {
                        ThermalSync.HeatSettings = MyAPIGateway.Utilities.SerializeFromXML<GlobalHeatSettings>(buffer.ReadToEnd());
                    }
                }
                else
                {
                    MyLog.Default.WriteLineAndConsole($"No Default Global Heat Generation File found. Creating one.");
                    ThermalSync.HeatSettings = new GlobalHeatSettings
                    {
                        AtmosphericDensity = 1,
                        GravityMultiplier = 1,
                        WeatherMultiplier = 1,
						NebulaMultiplier = 0.5f, //1.0 means thermal signature is totally gone if in 1.0 nebula
                        LargeGridBaseRange = 12500,
                        SmallGridBaseRange = 7500
                    };

                    var serialSet = MyAPIGateway.Utilities.SerializeToXML(ThermalSync.HeatSettings);

                    using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(defaultfile, typeof(GlobalHeatSettings)))
                    {
                        writer.Write(serialSet);
                    }

                }
				*/
				ThermalSync.HeatSettings = new GlobalHeatSettings
				{
					AtmosphericDensity = 1,
					GravityMultiplier = 1,
					WeatherMultiplier = 1,
					NebulaMultiplier = 0.5f, //1.0 means thermal signature is totally gone if in 1.0 nebula
					LargeGridBaseRange = 12500,
					SmallGridBaseRange = 7500
				};

                var heatfile = "HeatGenerators.xml";
/*                 if (MyAPIGateway.Utilities.FileExistsInLocalStorage(heatfile, typeof(List<HeatGenerator>)))
                {
                    MyLog.Default.WriteLineAndConsole($"Loading Heat Generators from HeatGenerators.xml");
                    using (var buffer = MyAPIGateway.Utilities.ReadFileInLocalStorage(heatfile, typeof(List<HeatGenerator>)))
                    {
                        ThermalSync.HeatGenerators = MyAPIGateway.Utilities.SerializeFromXML<List<HeatGenerator>>(buffer.ReadToEnd());
						ThermalSync.Send(new MessagePacket { heatGenerators = ThermalSync.HeatGenerators, settings = ThermalSync.HeatSettings });
                    }
                } */
				if (false) {
				}
                else
                {
                    MyLog.Default.WriteLineAndConsole($"No Default Heat Generation File found. Creating one.");

                    var tempGenerators = new List<HeatGenerator>
					{
						new HeatGenerator{
							SubtypeId = "",
							BlockCategory = BlockCategory.reactor,
							HeatOutput = 1,
							AirMultiplier = 0.5f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						},

						new HeatGenerator{
							SubtypeId = "",
							BlockCategory = BlockCategory.solar,
							HeatOutput = 0,
							AirMultiplier = 1f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						},

						new HeatGenerator{
							SubtypeId = "engine",
							BlockCategory = BlockCategory.hydrogen,
							HeatOutput = 0.5f,
							AirMultiplier = 0.05f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						},

						new HeatGenerator{
							SubtypeId = "",
							BlockCategory = BlockCategory.battery,
							HeatOutput = 0.25f,
							AirMultiplier = 0.50f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						},

						new HeatGenerator{
							SubtypeId = "",
							BlockCategory = BlockCategory.wind,
							HeatOutput = 0.0f,
							AirMultiplier = 0.25f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						},

						new HeatGenerator{
							SubtypeId = "hydrogen",
							BlockCategory = BlockCategory.thrust,
							HeatOutput = 0.50f,
							AirMultiplier = 1f,
							WeatherMultiplier = 1,
							GridSize = GridSize.all,
							SmallGridMultiplier = 0.5f
						}
					};


                    var serial = MyAPIGateway.Utilities.SerializeToXML(tempGenerators);
                    using (var writer = MyAPIGateway.Utilities.WriteFileInLocalStorage(heatfile, typeof(List<HeatGenerator>)))
                    {
                        writer.Write(serial);
                    }
                    ThermalSync.HeatGenerators = tempGenerators;
                    ThermalSync.Send(new MessagePacket { heatGenerators = ThermalSync.HeatGenerators, settings = ThermalSync.HeatSettings });
                }
            }else
            {

            }


        }
        
      
        [Serializable]
        public enum BlockCategory
        {
            power,thrust,reactor,hydrogen,wind,solar,battery
        }
        [Serializable]
        public enum GridSize
        { 
            small,large,station, mobile, all
        }


    }
    //[MyEntityComponentDescriptor(typeof(MyObjectBuilder_Beacon), false, "DetectionSmallBlockBeacon", "DetectionLargeBlockBeacon")]
    //public class BeaconDetect : MyGameLogicComponent
    //{

    //    IMyTerminalBlock Beacon;
    //    private List<IMyPowerProducer> PowerProducers = new List<IMyPowerProducer>();
    //    private List<IMyEntity> ThermalProducers = new List<IMyEntity>();
    //    private MyObjectBuilder_EntityBase m_objectBuilder;
    //    private MyDefinitionId electricity = MyResourceDistributorComponent.ElectricityId;
    //    private IMyCubeGrid CubeGrid = null;
    //    private bool isLargeGrid;
    //    private bool unloadHandlers = false;
    //    private DateTime lastRun;
    //    private float lastOutput = 0;
    //    public override void Init(MyObjectBuilder_EntityBase objectBuilder)
    //    {
    //        m_objectBuilder = objectBuilder;
    //        NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
    //    }

    //    private void CubeGrid_UpdatePowerGrid(IMySlimBlock obj)
    //    {

    //        CubeGrid = obj.CubeGrid;
    //        var gts = MyAPIGateway.TerminalActionsHelper.GetTerminalSystemForGrid(CubeGrid);
    //        gts.GetBlocksOfType(PowerProducers, block =>
    //        {
    //            if (block.IsSameConstructAs(Beacon))
    //            {
    //                return true;
    //            }else
    //            {
    //                return false;
    //            }
    //        });

    //        gts.GetBlocksOfType(ThermalProducers, block =>
    //        {
    //            if (block is IMyThrust)
    //            {
    //                return true;
    //            }
    //            else
    //            {
    //                return false;
    //            }
    //        });


    //        if (PowerProducers.Count != 0 || ThermalProducers.Count != 0)
    //        {
    //            CubeGrid.OnBlockAdded += CubeGrid_RefreshThermalGenerators;
    //            CubeGrid.OnBlockRemoved += CubeGrid_RefreshThermalGenerators;
    //            CubeGrid.OnBlockIntegrityChanged += CubeGrid_RefreshThermalGenerators;
    //            unloadHandlers = true;
    //        }
    //    }

    //    private void CubeGrid_RefreshThermalGenerators(IMySlimBlock obj)
    //    {
    //        if (obj is IMyBeacon)
    //        {
    //            Beacon_CheckThermal((IMyEntity)obj);
    //        }
    //    }

    //    public override void UpdateAfterSimulation100()
    //    {
    //        if (lastRun == DateTime.MinValue || DateTime.Now > lastRun.AddSeconds(5))
    //        {
    //            lastRun = DateTime.Now;
    //            NeedsUpdate = MyEntityUpdateEnum.EACH_100TH_FRAME;
    //            if (Beacon == null)
    //            {
    //                Beacon = Entity as IMyTerminalBlock;
    //                //grid maintenance. 
    //                CubeGrid = Beacon.CubeGrid;
    //                isLargeGrid = CubeGrid.GridSizeEnum == MyCubeSize.Large;
    //            }

    //            if (PowerProducers.Count == 0 || ThermalProducers.Count == 0)
    //            {
    //                CubeGrid_UpdatePowerGrid(Beacon.SlimBlock);
    //            }

    //            if (Beacon != null)
    //            {
    //                try
    //                {

    //                    if (Beacon != null && Beacon.IsWorking)
    //                    {
    //                        Beacon_CheckThermal(Beacon);
    //                    }
    //                }
    //                catch (Exception exc)
    //                {
    //                }

    //            }
    //        }

    //    }


    //    private void Beacon_CheckThermal(VRage.ModAPI.IMyEntity obj)
    //    {
    //        var subtype = Beacon.Name;
    //        if (obj is IMyBeacon)
    //        {
    //            var output = calculateRadius(GetThermalOutput(Beacon));
    //            var beacon = obj as IMyBeacon;
    //            if (lastOutput > output)
    //            {
    //                output = lastOutput * 0.90f;
    //            }
    //            lastOutput = output;
    //            beacon.Radius = output;
    //            beacon.Enabled = true;
    //            beacon.HudText = "Thermal Signature";
    //        }
    //    }

    //    private float calculateRadius (float EnergyinMW)
    //    {
    //        float radius = 0.0f;
    //        if (isLargeGrid)
    //        {
    //            radius = EnergyinMW / 300 * 25000;
    //        }else
    //        {
    //            radius = EnergyinMW / 30 * 15000;
    //        }
    //        return radius;
    //    }

    //    public override void OnRemovedFromScene()
    //    {

    //        base.OnRemovedFromScene();
    //        if (unloadHandlers)
    //        {
    //            CubeGrid.OnBlockAdded -= CubeGrid_RefreshThermalGenerators;
    //            CubeGrid.OnBlockRemoved -= CubeGrid_RefreshThermalGenerators;
    //            CubeGrid.OnBlockIntegrityChanged -= CubeGrid_RefreshThermalGenerators;
    //        }
    //    }
    //    private float GetThermalOutput(IMyTerminalBlock block)
    //    {
    //        var thermalOutput = 0.0f;
    //        if (block.CubeGrid.IsStatic)
    //        {
    //            return 0.0f;
    //        }            

    //        if (PowerProducers.Count != 0)
    //        {
    //            foreach (var powerProducer in PowerProducers)
    //            {
    //                if (powerProducer is IMyReactor)
    //                {
    //                    thermalOutput += powerProducer.CurrentOutput;
    //                }
    //                else if (powerProducer is IMyBatteryBlock)
    //                {
    //                    thermalOutput += powerProducer.CurrentOutput * 0.25f;
    //                }
    //                else if (powerProducer is IMySolarPanel)
    //                {
    //                    thermalOutput += 0;
    //                }
    //                else if (powerProducer.BlockDefinition.SubtypeId.ToLower().Contains("engine")) //because thank you, Keen.
    //                {
    //                    thermalOutput += powerProducer.CurrentOutput * 0.5f;
    //                }
    //                else  //Wind?
    //                {
    //                    thermalOutput += 0;
    //                }
    //            }
    //        }

    //        if (ThermalProducers.Count != 0)
    //        {
    //            foreach (var thermalProducer in ThermalProducers)
    //            {

    //                //thruster logic
    //                if (thermalProducer is IMyThrust)
    //                {
    //                    var thrust = thermalProducer as IMyThrust;
    //                    if (thrust.BlockDefinition.SubtypeId.ToLower().Contains("hydrogen"))
    //                    {
    //                        thermalOutput += (thrust.CurrentThrust / 6000000) * 67; //large grid, large thruster
    //                    }
    //                }
    //            }
    //        }
    //        return thermalOutput ;
    //    }

    //}
}
