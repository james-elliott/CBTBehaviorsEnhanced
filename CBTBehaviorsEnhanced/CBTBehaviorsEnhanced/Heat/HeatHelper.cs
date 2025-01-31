﻿
using BattleTech;
using CBTBehaviorsEnhanced.Components;
using CustAmmoCategories;
using CustomComponents;
using IRBTModUtils.Extension;
using MechEngineer.Features.ComponentExplosions;
using System;
using System.Collections.Generic;
using UnityEngine;
using us.frostraptor.modUtils;

namespace CBTBehaviorsEnhanced {
    public static class HeatHelper {

        /* CAC manipulates a unit's heat scale multiple times during their activation. See ApplyHeatSinks.cs for most of the logic.
         * When it applies a heat event, it consumes a certain amount of HeatSinkCapacity, and records the remainder in CurrentHeat.
         * It records the consumed capacity in UsedHeatSinksCap(), which can be accessed through the extension UsedHeatSinksCap().
         * Because of this, tracking heat and capacity values requires normalizing these disparate units back into
         * a linear scale. This is necessary for our purposes, as we want to show the player a prediction of what's going to occur.
         */
        public static int NormalizedHeatSinkCapacity(this Mech mech) {
            // Total HeatSinkCapacity is HeatSinkCapacity (patched by KMission) + UsedHeatSinksCap
            int totalHeatSinkCap = mech.HeatSinkCapacity + mech.UsedHeatSinksCap();
            Mod.HeatLog.Trace?.Write($"Mech: {CombatantUtils.Label(mech)} has totalHeatSinkCap: {totalHeatSinkCap} from currentCap: {mech.HeatSinkCapacity} + usedCap: {mech.UsedHeatSinksCap()}");
            return totalHeatSinkCap;
        }

        /* Because CAC changes HeatSinkCapacity, we can't rely on AdjustedHeatSinkCapacity. AdjustedHeatSinkCapacity invokes HeatSinkCapacity, so you'll only get
         * the remainder capacity modified by the designMask, which would be difficult to normalize. Instead, just replay the logic from Mech::AdjustedHeatSinkCapacity
         * on NormalizedHeatSinkCapacity to ensure an accurate prediction.  
         */
        public static int NormalizedAdjustedHeatSinkCapacity(this Mech mech, bool isProjectedHeat, bool fractional) {

            int capacity = fractional ? mech.HeatSinkCapacity : mech.NormalizedHeatSinkCapacity();
            float adjustedNormalizedCap = (float)capacity * mech.DesignMaskHeatMulti(isProjectedHeat);
            Mod.HeatLog.Trace?.Write($"Mech: {CombatantUtils.Label(mech)} has adjustedNormedCap: {adjustedNormalizedCap} from normedCap: {capacity} x multi: {mech.DesignMaskHeatMulti(isProjectedHeat)}");

            return (int)adjustedNormalizedCap;
        }

        // Replicates logic from Mech::AdjustedHeatSinkCapacity to allow displaying multiplier
        public static float DesignMaskHeatMulti(this Mech mech, bool isProjectedHeat) {
            float capacityMulti = 1f;

            try {
                // Check for currently occupied, or future
                if (isProjectedHeat) {
                    Mod.HeatLog.Trace?.Write("Calculating projected position heat.");
                    if (mech.Pathing != null && mech.Pathing.CurrentPath != null && mech.Pathing.CurrentPath.Count > 0) {

                        // Determine the destination designMask
                        Mod.HeatLog.Trace?.Write($"CurrentPath has: {mech.Pathing.CurrentPath.Count} nodes, using destination path: {mech.Pathing.ResultDestination}");
                        DesignMaskDef destinationDesignMaskDef = mech?.Combat?.MapMetaData?.GetPriorityDesignMaskAtPos(mech.Pathing.ResultDestination);
                        if (destinationDesignMaskDef != null && !Mathf.Approximately(destinationDesignMaskDef.heatSinkMultiplier, 1f)) {
                            Mod.HeatLog.Trace?.Write($"Destination design mask: {destinationDesignMaskDef?.Description?.Name} has heatSinkMulti: x{destinationDesignMaskDef?.heatSinkMultiplier} ");
                            capacityMulti *= destinationDesignMaskDef.heatSinkMultiplier;
                        }

                        // Check for any cells along the way that will apply the burning sticky effect.
                        //   See CustomAmmoCategories\designmask\DesignMaskBurningForest
                        List<WayPoint> waypointsFromPath = ActorMovementSequence.ExtractWaypointsFromPath(
                            mech, mech.Pathing.CurrentPath, mech.Pathing.ResultDestination, (ICombatant)mech.Pathing.CurrentMeleeTarget, mech.Pathing.MoveType
                            );
                        List<MapTerrainCellWaypoint> terrainWaypoints = DynamicMapHelper.getVisitedWaypoints(mech.Combat, waypointsFromPath);
                        Mod.HeatLog.Trace?.Write($"  Count of waypointsFromPath: {waypointsFromPath?.Count}  terrainWaypoints: {terrainWaypoints?.Count}");

                        // This assumes 1) only KMission is using stickyEffects that modify HeatSinkCapacity and 2) it has a stackLimit of 1. Anything else will break this.
                        float stickyModifier = 1f;
                        foreach (MapTerrainCellWaypoint cell in terrainWaypoints) {
                            if (cell != null && cell?.cell?.BurningStrength > 0 && cell?.cell?.mapMetaData?.designMaskDefs != null) {
                                Mod.HeatLog.Trace?.Write($"  checking burningCell for designMask.");
                                foreach (DesignMaskDef cellDesignMaskDef in cell?.cell?.mapMetaData?.designMaskDefs?.Values) {
                                    Mod.HeatLog.Trace?.Write($"    checking designMask for stickyEffects.");
                                    if (cellDesignMaskDef.stickyEffect != null && cellDesignMaskDef.stickyEffect?.statisticData != null &&
                                        cellDesignMaskDef.stickyEffect.statisticData.statName == ModStats.HBS_HeatSinkCapacity) {
                                        Mod.HeatLog.Trace?.Write($"      found stickyEffects.");
                                        stickyModifier = Single.Parse(cellDesignMaskDef.stickyEffect.statisticData.modValue);
                                    }
                                }
                            }
                        }
                        if (!Mathf.Approximately(stickyModifier, 1f)) {
                            capacityMulti *= stickyModifier;
                            Mod.HeatLog.Trace?.Write($"  capacityMulti: {capacityMulti} after stickyModifier: {stickyModifier}");
                        }

                    } else {
                        Mod.HeatLog.Trace?.Write($"Current path is null or has 0 count, skipping.");
                    }
                } else {
                    Mod.HeatLog.Trace?.Write("Calculating current position heat.");
                    if (mech.occupiedDesignMask != null && !Mathf.Approximately(mech.occupiedDesignMask.heatSinkMultiplier, 1f)) {
                        Mod.HeatLog.Trace?.Write($"Multi for currentPos is: {mech?.occupiedDesignMask?.heatSinkMultiplier}");
                        capacityMulti *= mech.occupiedDesignMask.heatSinkMultiplier;
                    }

                }

                if (mech?.Combat?.MapMetaData?.biomeDesignMask != null && !Mathf.Approximately(mech.Combat.MapMetaData.biomeDesignMask.heatSinkMultiplier, 1f)) {
                    Mod.HeatLog.Trace?.Write($"Biome: {mech.Combat.MapMetaData.biomeDesignMask.Id} has heatSinkMulti: x{mech.Combat.MapMetaData.biomeDesignMask.heatSinkMultiplier} ");
                    capacityMulti *= mech.Combat.MapMetaData.biomeDesignMask.heatSinkMultiplier;
                }
            } catch (Exception e) {
                Mod.HeatLog.Error?.Write(e, $"Failed to calculate designMaskHeatMulti due to error: {e}");
            }
            
            Mod.HeatLog.Trace?.Write($"Calculated capacityMulti: {capacityMulti} x globalHeatSinkMulti: {mech.Combat.Constants.Heat.GlobalHeatSinkMultiplier} ");
            capacityMulti *= mech.Combat.Constants.Heat.GlobalHeatSinkMultiplier;
            return capacityMulti;
        }

        /* CAC introduces burning effects, which applies heat for each hex you move through, and if you end your turn in a burning hex.
         *   Calculate this heat so we can show it to the player
         *   TODO: Handle unaffected by fire
         */
        public static int CACTerrainHeat(this Mech mech) {
            float terrainHeat = 0f;

            // If the unit has been marked as not being affected by fire, skip it entirely 
            if (mech.UnaffectedFire()) { return 0; }

            if (mech.Pathing.CurrentPath != null && mech.Pathing.CurrentPath.Count > 0) {

                List<WayPoint> waypointsFromPath = ActorMovementSequence.ExtractWaypointsFromPath(
                    mech, mech.Pathing.CurrentPath, mech.Pathing.ResultDestination, (ICombatant)mech.Pathing.CurrentMeleeTarget, mech.Pathing.MoveType
                    );
                List<MapTerrainCellWaypoint> terrainWaypoints = DynamicMapHelper.getVisitedWaypoints(mech.Combat, waypointsFromPath);
                Mod.HeatLog.Trace?.Write($"  Count of waypointsFromPath: {waypointsFromPath.Count}  terrainWaypoints: {terrainWaypoints.Count}");

                float sumOfCellHeat = 0f;
                int totalCells = 0;
                foreach (MapTerrainCellWaypoint cell in terrainWaypoints) {
                    if (cell != null && cell.cell.BurningStrength > 0) {
                        Mod.HeatLog.Trace?.Write($" --Adding {cell.cell.BurningStrength} heat from cell at worldPos: {cell.cell.WorldPos()}");
                        sumOfCellHeat += cell.cell.BurningStrength;
                        totalCells += 1;
                    }
                }
                terrainHeat = totalCells != 0 ? (float)Math.Ceiling(sumOfCellHeat / totalCells) : 0;
                Mod.HeatLog.Trace?.Write($"TerrainHeat: {terrainHeat} = sumOfHeat: {sumOfCellHeat} / totalCells: {totalCells}");
            } else {
                MapTerrainDataCellEx cell = mech.Combat.MapMetaData.GetCellAt(mech.CurrentPosition) as MapTerrainDataCellEx;
                if (cell != null && cell.BurningStrength > 0) {
                    Mod.HeatLog.Trace?.Write($"Adding {cell.BurningStrength} heat from current position: {mech.CurrentPosition}");
                    terrainHeat = cell.BurningStrength;
                }
            }

            return (int)Math.Ceiling(terrainHeat);
        }

        public class CalculatedHeat {
            public int CurrentHeat;
            public int ProjectedHeat;
            public int TempHeat;

            public int CACTerrainHeat;
            public int CurrentPathNodes;
            public bool IsProjectedHeat;

            public int SinkableHeat;
            public int OverallSinkCapacity;
            public int FutureHeat;
            public int ThresholdHeat;
        }

        public static CalculatedHeat CalculateHeat(Mech mech, int projectedHeat) {
            // Calculate the heat from CAC burning 
            int cacTerrainHeat = mech.CACTerrainHeat();
            int currentPathNodes = mech.Pathing != null && mech.Pathing.CurrentPath != null ? mech.Pathing.CurrentPath.Count : 0;

            bool isProjectedHeat = projectedHeat != 0 || currentPathNodes != 0;
            int sinkableHeat = mech.NormalizedAdjustedHeatSinkCapacity(isProjectedHeat, true) * -1;
            int overallSinkCapacity = mech.NormalizedAdjustedHeatSinkCapacity(isProjectedHeat, false) * -1;
            Mod.HeatLog.Trace?.Write($"  remainingCapacity: {mech.HeatSinkCapacity}  rawCapacity: {overallSinkCapacity}  normedAdjustedFraction: {sinkableHeat}");

            int currentHeat = mech.CurrentHeat;
            int tempHeat = mech.TempHeat;

            int futureHeat = Math.Max(0, currentHeat + tempHeat + projectedHeat + cacTerrainHeat);
            if (futureHeat > Mod.Config.Heat.MaxHeat) futureHeat = Mod.Config.Heat.MaxHeat;
            Mod.HeatLog.Trace?.Write($"  currentHeat: {currentHeat} + tempHeat: {tempHeat} + projectedHeat: {projectedHeat} + cacTerrainheat: {cacTerrainHeat} + sinkableHeat: {sinkableHeat}" +
                $"  =  futureHeat: {futureHeat}");

            int thresholdHeat = Math.Max(0, futureHeat + sinkableHeat);
            if (thresholdHeat > Mod.Config.Heat.MaxHeat) thresholdHeat = Mod.Config.Heat.MaxHeat;
            Mod.HeatLog.Trace?.Write($"Threshold heat: {thresholdHeat} = futureHeat: {futureHeat} + sinkableHeat: {sinkableHeat}");

            return new CalculatedHeat {
                CurrentHeat = currentHeat,
                ProjectedHeat = projectedHeat,
                TempHeat = tempHeat,
                CACTerrainHeat = cacTerrainHeat,
                CurrentPathNodes = currentPathNodes,
                IsProjectedHeat = isProjectedHeat,
                SinkableHeat = sinkableHeat,
                OverallSinkCapacity = overallSinkCapacity,
                FutureHeat = futureHeat,
                ThresholdHeat = thresholdHeat
            };
        }

        public static AmmunitionBox FindMostDamagingAmmoBox(Mech mech, bool isVolatile) {
            Mod.HeatLog.Debug?.Write($"Checking mech: {mech.DistinctId()} for ammo w/ isVolatile: {isVolatile}");
            float totalDamage = 0f;
            AmmunitionBox mostDangerousBox = null;
            foreach (AmmunitionBox ammoBox in mech.ammoBoxes) {
                Mod.HeatLog.Debug?.Write($" -- ammoBox: {ammoBox.UIName}");

                if (ammoBox.IsFunctional == false) {
                    Mod.HeatLog.Debug?.Write($" -- ammobox is not functional, skipping."); 
                    continue; 
                }

                if (ammoBox.CurrentAmmo <= 0) {
                    Mod.HeatLog.Debug?.Write($" -- ammobox  has no ammo, skipping.");
                    continue; 
                }

                if (!ammoBox.mechComponentRef.Is<VolatileAmmo>(out VolatileAmmo vAmmo) && isVolatile)
                {
                    Mod.HeatLog.Debug?.Write($"  -- ammobox  is not a volatile ammo, skipping.");
                    continue;
                }

                // TODO: Make a config setting
                float boxDamage = ammoBox.CurrentAmmo * 1;
                if (ModState.MEIsLoaded)
                {
                    boxDamage = CalcMEAmmoDamage(ammoBox);
                    if (boxDamage == -1)
                        continue;
                }

                // Multiply box damage by the volatile weighting
                if (vAmmo != null) {
                    boxDamage *= vAmmo.damageWeighting;
                }

                if (boxDamage > totalDamage)
                {
                    mostDangerousBox = ammoBox;
                    totalDamage = boxDamage;
                }
            }

            return mostDangerousBox;
        } 

        // This method is separated out from the main flow to prevent the ME assembly from being loaded. 
        //   C# IL won't load a type outside of a method invocation. Isolating the compile-time linkage in this
        //   method means we can gate the invocation with a hasME flag successfully.
        private static float CalcMEAmmoDamage(AmmunitionBox ammoBox)
        {
            float damage = ammoBox.CurrentAmmo * 1;

            if (ammoBox.mechComponentRef.Is<ComponentExplosion>(out ComponentExplosion compExp))
            {
                Mod.HeatLog.Debug?.Write($" AmmoBox: {ammoBox.UIName} has {ammoBox.CurrentAmmo} rounds with explosion/ammo: {compExp.ExplosionDamagePerAmmo} " +
                    $"heat/ammo: {compExp.HeatDamagePerAmmo} stab/ammo: {compExp.StabilityDamagePerAmmo}");
                damage = (ammoBox.CurrentAmmo * compExp.HeatDamagePerAmmo) + (ammoBox.CurrentAmmo * compExp.ExplosionDamagePerAmmo) + (ammoBox.CurrentAmmo * compExp.StabilityDamagePerAmmo);
            }
            else
            {
                Mod.HeatLog.Debug?.Write($"  AmmoBox: {ammoBox.UIName} is not configured as a ME ComponentExplosion, skipping.");
                //  A return value of -1 means this isn't a valid ME ammo box, and it should be skipped
                damage = -1;
            }

            return damage;
        }

    }
}
