﻿using BattleTech;
using CBTBehaviorsEnhanced.Helper;
using Harmony;
using Localize;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using us.frostraptor.modUtils;

namespace CBTBehaviorsEnhanced.HullIntegrity {

    // Apply hull integrity breaches as per Tac-Ops pg. 54. 

    [HarmonyPatch(typeof(AttackDirector), "OnAttackSequenceBegin")]
    public static class AttackDirector_OnAttackSequenceBegin {
        public static bool Prepare() { return Mod.Config.Features.BiomeBreaches; }

        public static void Postfix(AttackDirector __instance, MessageCenterMessage message) {
            Mod.HullBreachLog.Trace?.Write("AD:OASB - entered.");

            AttackSequenceBeginMessage asbMessage = message as AttackSequenceBeginMessage;
            if (__instance == null || asbMessage == null) return;

            int sequenceId = asbMessage.sequenceId;
            AttackDirector.AttackSequence attackSequence = __instance.GetAttackSequence(sequenceId);

            if (attackSequence == null)
            {
                Mod.HullBreachLog.Debug?.Write($"AttackSequence not found - created by a mod or CAC watchdog killed it. Skipping!");
                ModState.BreachAttackId = ModState.NO_ATTACK_SEQUENCE_ID;
            }

            // No chosen target, nothing to damage
            if (attackSequence.chosenTarget == null) return;

            Mod.HullBreachLog.Debug?.Write($"Recording attackSequence: {attackSequence.id} for possible hull breaches.");
            ModState.BreachAttackId = attackSequence.id;
        }
    }
    
    [HarmonyPatch(typeof(AttackDirector), "OnAttackSequenceEnd")]
    public static class AttackDirector_OnAttackSequenceEnd {
        public static bool Prepare() { return Mod.Config.Features.BiomeBreaches; }

        public static void Postfix(AttackDirector __instance, MessageCenterMessage message) {
            Mod.HullBreachLog.Trace?.Write("AD:OASE - entered.");

            // Nothing to do
            if (ModState.BreachAttackId == ModState.NO_ATTACK_SEQUENCE_ID) return;

            AttackSequenceEndMessage attackSequenceEndMessage = message as AttackSequenceEndMessage;
            
            if (__instance == null || attackSequenceEndMessage == null) return; 

            int sequenceId = attackSequenceEndMessage.sequenceId;
            AttackDirector.AttackSequence attackSequence = __instance.GetAttackSequence(sequenceId);

            if (attackSequence == null) {
                Mod.HullBreachLog.Info?.Write($"Could not find attack sequence with id: {sequenceId}. CAC may have killed it, or there's an error in processing. Skipping hull breach checks.");
                return;
            }

            // No chosen target, nothing to damage
            if (attackSequence.chosenTarget == null) return;

            if (ModState.BreachAttackId != attackSequence.id) {
                Mod.HullBreachLog.Debug?.Write($"Attack sequence ID {attackSequence.id} does not match Hull Breach Attack Id - skipping hull breach resolution.");
                return;
            }

            float structureDamage = attackSequence.GetStructureDamageDealt(attackSequence.chosenTarget.GUID);
            Mod.HullBreachLog.Debug?.Write($"Attack sequence {sequenceId} did {structureDamage} structure damage to target: {attackSequence.chosenTarget.GUID}");
            if (structureDamage == 0f) {
                Mod.HullBreachLog.Debug?.Write($"Attack did no structure damage, skipping.");
                return;
            }

            if (attackSequence.chosenTarget is Mech targetMech) {
                Mod.HullBreachLog.Debug?.Write($"Checking hull breaches for targetMech: {CombatantUtils.Label(targetMech)}");
                ResolveMechHullBreaches(targetMech);
            }
            if (attackSequence.chosenTarget is Turret targetTurret) {
                Mod.HullBreachLog.Debug?.Write($"Checking hull breaches for targetTurret: {CombatantUtils.Label(targetTurret)}");
                ResolveTurretHullBreaches(targetTurret);
            }
            if (attackSequence.chosenTarget is Vehicle targetVehicle) {
                Mod.HullBreachLog.Debug?.Write($"Checking hull breaches for targetVehicle: {CombatantUtils.Label(targetVehicle)}");
                ResolveVehicleHullBreaches(targetVehicle);
            }

            // Reset state
            ModState.BreachAttackId = ModState.NO_ATTACK_SEQUENCE_ID;
            ModState.BreachHitsMech.Clear();
            ModState.BreachHitsTurret.Clear();
            ModState.BreachHitsVehicle.Clear();

            Mod.HullBreachLog.Trace?.Write("AD:OASE - exiting.");
        }

        // Resolve mech hits - mark components invalid, but kill the pilot on a head-hit
        private static void ResolveMechHullBreaches(Mech targetMech) {
            bool needsQuip = false;
            foreach (ChassisLocations hitLocation in ModState.BreachHitsMech.Keys) {
                List<MechComponent> componentsInLocation =
                    targetMech.allComponents.Where(mc => mc.DamageLevel == ComponentDamageLevel.Functional && 
                    mc.mechComponentRef.MountedLocation == hitLocation).ToList();

                // Check for immunity in this location
                bool hasImmunity = false;
                foreach (MechComponent mc in componentsInLocation) {
                    if (mc.StatCollection.ContainsStatistic(ModStats.HullBreachImmunity)) {
                        Mod.HullBreachLog.Info?.Write($"  Component: {mc.UIName} grants hull breach immunity to {CombatantUtils.Label(targetMech)}, skipping!");
                        hasImmunity = true;
                        break;
                    }
                }
                if (hasImmunity) { continue; }

                // If no immunity, sum the breach check across all trials
                float passChance = 1f - ModState.BreachCheck;
                //float sequencePassChance = Mathf.Pow(passChance, ModState.BreachHitsMech[hitLocation]);
                // TODO: Number of trials is way too rough, and can make breaches extremely common. Weakening to flat percentage chance.
                float sequencePassChance = Mathf.Pow(passChance, 1);
                float sequenceThreshold = 1f - sequencePassChance;
                Mod.HullBreachLog.Debug?.Write($" For pass chance: {passChance} with n trials: {ModState.BreachHitsMech[hitLocation]} has sequencePassChance: {sequencePassChance} => sequenceThreshold: {sequenceThreshold}");

                // Check for failure
                bool passedCheck = CheckHelper.DidCheckPassThreshold(sequenceThreshold, targetMech, 0f, Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]);
                Mod.HullBreachLog.Debug?.Write($"Actor: {CombatantUtils.Label(targetMech)} HULL BREACH check: {passedCheck} for location: {hitLocation}");
                if (!passedCheck) {
                    Mod.HullBreachLog.Info?.Write($" Mech {CombatantUtils.Label(targetMech)} suffers a hull breach in location: {hitLocation}");

                    string floatieText = new Text(Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]).ToString();
                    MultiSequence showInfoSequence = new ShowActorInfoSequence(targetMech, floatieText, FloatieMessage.MessageNature.Debuff, false);
                    targetMech.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(showInfoSequence));

                    needsQuip = true;

                    if (hitLocation <= ChassisLocations.RightTorso) {
                        switch (hitLocation) {
                            case ChassisLocations.Head:
                                Mod.HullBreachLog.Info?.Write($"  Head structure damage taken, killing pilot!");
                                targetMech.GetPilot().KillPilot(targetMech.Combat.Constants, "", 0, DamageType.Enemy, null, null);
                                break;
                            case ChassisLocations.CenterTorso:
                            default:
                                if (hitLocation == ChassisLocations.CenterTorso) 
                                { 
                                    Mod.HullBreachLog.Info?.Write($"  Center Torso hull breach, unit should die from damage processing!"); 
                                }
                                // Walk the location and disable every component in it
                                foreach (MechComponent mc in componentsInLocation) {
                                    Mod.HullBreachLog.Info?.Write($"  Marking component: {mc.defId} of type: {mc.componentDef.Description.Name} nonfunctional due to hull breach.");
                                    mc.DamageComponent(default(WeaponHitInfo), ComponentDamageLevel.NonFunctional, true);
                                }
                                break;
                        }
                    }
                }
            }
            if (needsQuip) { QuipHelper.PublishQuip(targetMech, Mod.LocalizedText.Quips.Breach); }
        }

        // Resolve turret hits - any hull breach kill the unit
        private static void ResolveTurretHullBreaches(Turret targetTurret) {
            bool needsQuip = false;

            // Check for immunity
            List<MechComponent> components =
                targetTurret.allComponents.Where(mc => mc.DamageLevel == ComponentDamageLevel.Functional).ToList();
            bool hasImmunity = false;
            foreach (MechComponent mc in components) {
                if (mc.StatCollection.ContainsStatistic(ModStats.HullBreachImmunity)) {
                    Mod.HullBreachLog.Debug?.Write($"  Component: {mc.UIName} grants hull breach immunity, skipping!");
                    hasImmunity = true;
                    break;
                }
            }
            if (hasImmunity) { return; }

            foreach (BuildingLocation hitLocation in ModState.BreachHitsTurret.Keys) {
                // If no immunity, sum the breach check across all trials
                float passChance = 1f - ModState.BreachCheck;
                //float sequencePassChance = Mathf.Pow(passChance, ModState.BreachHitsTurret[hitLocation]);
                // TODO: Number of trials is way too rough, and can make breaches extremely common. Weakening to flat percentage chance.
                float sequencePassChance = Mathf.Pow(passChance, 1);
                float sequenceThreshold = 1f - sequencePassChance;
                Mod.HullBreachLog.Debug?.Write($" For pass chance: {passChance} with n trials: {ModState.BreachHitsTurret[hitLocation]} has sequencePassChance: {sequencePassChance} => sequenceThreshold: {sequenceThreshold}");

                // Check for failure
                bool passedCheck = CheckHelper.DidCheckPassThreshold(sequenceThreshold, targetTurret, 0f, Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]);
                Mod.HullBreachLog.Debug?.Write($"Actor: {CombatantUtils.Label(targetTurret)} HULL BREACH check: {passedCheck} for location: {hitLocation}");
                if (!passedCheck) {
                    Mod.HullBreachLog.Info?.Write($" Turret {CombatantUtils.Label(targetTurret)} suffers a hull breach in location: {hitLocation}");

                    string floatieText = new Text(Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]).ToString();
                    MultiSequence showInfoSequence = new ShowActorInfoSequence(targetTurret, floatieText, FloatieMessage.MessageNature.Debuff, false);
                    targetTurret.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(showInfoSequence));

                    needsQuip = true;

                    if (targetTurret.GetPilot() != null) {
                        targetTurret.GetPilot().KillPilot(targetTurret.Combat.Constants, "", 0, DamageType.Unknown, null, null);
                    }
                    targetTurret.FlagForDeath("Dead from hull breach!", DeathMethod.Unknown, DamageType.Unknown, -1, -1, "", false);
                    targetTurret.HandleDeath("0");
                }
            }
            if (needsQuip) { QuipHelper.PublishQuip(targetTurret, Mod.LocalizedText.Quips.Breach); }
        }

        private static void ResolveVehicleHullBreaches(Vehicle targetVehicle) {
            bool needsQuip = false;

            foreach (VehicleChassisLocations hitLocation in ModState.BreachHitsVehicle.Keys) {
                List<MechComponent> componentsInLocation =
                    targetVehicle.allComponents.Where(mc => mc.DamageLevel == ComponentDamageLevel.Functional && 
                    mc.vehicleComponentRef.MountedLocation == (VehicleChassisLocations)hitLocation).ToList();

                // Check for immunity in this location
                bool hasImmunity = false;
                foreach (MechComponent mc in componentsInLocation) {
                    if (mc.StatCollection.ContainsStatistic(ModStats.HullBreachImmunity)) {
                        Mod.HullBreachLog.Debug?.Write($"  Component: {mc.UIName} grants hull breach immunity, skipping!");
                        hasImmunity = true;
                        break;
                    }
                }
                if (hasImmunity) { continue; }

                // If no immunity, sum the breach check across all trials
                float passChance = 1f - ModState.BreachCheck;
                //float sequencePassChance = Mathf.Pow(passChance, ModState.BreachHitsVehicle[hitLocation]);
                // TODO: Number of trials is way too rough, and can make breaches extremely common. Weakening to flat percentage chance.
                float sequencePassChance = Mathf.Pow(passChance, 1);

                float sequenceThreshold = 1f - sequencePassChance;
                Mod.HullBreachLog.Debug?.Write($" For pass chance: {passChance} with n trials: {ModState.BreachHitsVehicle[hitLocation]} has sequencePassChance: {sequencePassChance} => sequenceThreshold: {sequenceThreshold}");

                // Check for failure
                bool passedCheck = CheckHelper.DidCheckPassThreshold(sequenceThreshold, targetVehicle, 0f, Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]);
                Mod.HullBreachLog.Debug?.Write($"Actor: {CombatantUtils.Label(targetVehicle)} HULL BREACH check: {passedCheck} for location: {hitLocation}");
                if (!passedCheck) {
                    Mod.HullBreachLog.Info?.Write($" Vehicle {CombatantUtils.Label(targetVehicle)} suffers a hull breach in location: {hitLocation}");

                    string floatieText = new Text(Mod.LocalizedText.Floaties[ModText.FT_Hull_Breach]).ToString();
                    MultiSequence showInfoSequence = new ShowActorInfoSequence(targetVehicle, floatieText, FloatieMessage.MessageNature.Debuff, false);
                    targetVehicle.Combat.MessageCenter.PublishMessage(new AddSequenceToStackMessage(showInfoSequence));

                    needsQuip = true;

                    if (targetVehicle.GetPilot() != null) {
                        targetVehicle.GetPilot().KillPilot(targetVehicle.Combat.Constants, "", 0, DamageType.Unknown, null, null);
                    }
                    targetVehicle.FlagForDeath("Dead from hull breach!", DeathMethod.Unknown, DamageType.Unknown, -1, -1, "", false);
                    targetVehicle.HandleDeath("0");
                    break;
                }
            }
            if (needsQuip) { QuipHelper.PublishQuip(targetVehicle, Mod.LocalizedText.Quips.Breach); }
        }
    }

    [HarmonyPatch(typeof(Mech), "ApplyStructureStatDamage")]
    public static class Mech_ApplyStructureStatDamage {
        public static bool Prepare() { return Mod.Config.Features.BiomeBreaches; }

        public static void Postfix(Mech __instance, ChassisLocations location, float damage, WeaponHitInfo hitInfo) {
            Mod.HullBreachLog.Trace?.Write("M:ASSD - entered.");

            if (ModState.BreachCheck == 0f || ModState.BreachAttackId == ModState.NO_ATTACK_SEQUENCE_ID) { return; } // nothing to do
            if (hitInfo.attackSequenceId != ModState.BreachAttackId) {
                Mod.HullBreachLog.Warn?.Write($"AttackSequenceId: {hitInfo.attackSequenceId} does not match hull breach attack sequence id: {ModState.BreachAttackId}... wasn't expecting this, skipping!");
                return;
            }
            
            Mod.HullBreachLog.Debug?.Write($" --- Location: {location} needs breach check.");
            if (ModState.BreachHitsMech.ContainsKey(location)) {
                ModState.BreachHitsMech[location]++;
            } else {
                ModState.BreachHitsMech.Add(location, 1);
            }
        }
    }

    [HarmonyPatch(typeof(Turret), "ApplyStructureStatDamage")]
    public static class Turret_ApplyStructureStatDamage {
        public static bool Prepare() { return Mod.Config.Features.BiomeBreaches; }

        public static void Postfix(Turret __instance, BuildingLocation location, float damage, WeaponHitInfo hitInfo) {
            Mod.HullBreachLog.Trace?.Write("T:ASSD - entered.");

            if (ModState.BreachCheck == 0f || ModState.BreachAttackId == ModState.NO_ATTACK_SEQUENCE_ID) { return; } // nothing to do
            if (hitInfo.attackSequenceId != ModState.BreachAttackId) {
                Mod.HullBreachLog.Warn?.Write($"AttackSequenceId: {hitInfo.attackSequenceId} does not match hull breach attack sequence id: {ModState.BreachAttackId}... wasn't expecting this, skipping!");
                return;
            }

            Mod.HullBreachLog.Debug?.Write($" --- Location: {location} needs breach check.");
            if (ModState.BreachHitsTurret.ContainsKey(location)) {
                ModState.BreachHitsTurret[location]++;
            } else {
                ModState.BreachHitsTurret.Add(location, 1);
            }
        }
    }

    // Yes, this is intentional. That's the actual function name
    [HarmonyPatch(typeof(Vehicle), "applyStructureStatDamage")]
    public static class Vehicle_ApplyStructureStatDamage {
        public static bool Prepare() { return Mod.Config.Features.BiomeBreaches; }

        public static void Postfix(Vehicle __instance, VehicleChassisLocations location, float damage, WeaponHitInfo hitInfo) {
            Mod.HullBreachLog.Trace?.Write("V:ASSD - entered.");

            if (ModState.BreachCheck == 0f || ModState.BreachAttackId == ModState.NO_ATTACK_SEQUENCE_ID) { return; } // nothing to do
            if (hitInfo.attackSequenceId != ModState.BreachAttackId) {
                Mod.HullBreachLog.Warn?.Write($"AttackSequenceId: {hitInfo.attackSequenceId} does not match hull breach attack sequence id: {ModState.BreachAttackId}... wasn't expecting this, skipping!");
                return;
            }

            Mod.HullBreachLog.Debug?.Write($" --- Location: {location} needs breach check.");
            if (ModState.BreachHitsVehicle.ContainsKey(location)) {
                ModState.BreachHitsVehicle[location]++;
            } else {
                ModState.BreachHitsVehicle.Add(location, 1);
            }
        }
    }

}
