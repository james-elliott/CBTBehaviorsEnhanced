﻿using BattleTech;
using Harmony;
using IRBTModUtils.Extension;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace CBTBehaviorsEnhanced.Patches.AI
{
    [HarmonyPatch]
    public static class CanDFAHostileTargetsNode_Tick_Patch
    {

        public static MethodBase TargetMethod()
        {
            var type = AccessTools.TypeByName("CanDFAHostileTargetsNode");
            return AccessTools.Method(type, "Tick");
        }

        static void Postfix(ref BehaviorTreeResults __result, string ___name, BehaviorTree ___tree, AbstractActor ___unit)
        {
            Mod.AILog.Info?.Write("CanDFAHostileTargetsNode:Tick - entered");

            Mod.AILog.Info?.Write($"CanDFAHostileTargetsNode returned {__result.nodeState} for unit: {___unit.DistinctId()}");
        }
    }

}
