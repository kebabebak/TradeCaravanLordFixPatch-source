using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace HSK.TradeCaravanLordFixPatch
{
    /// <summary>
    /// Problem: vanilla LordJob_TradeWithColony.CreateGraph captures FindTrader(lord) into a
    /// Trigger_Custom closure and reads trader.mindState.traderDismissed with no null check. When
    /// FindTrader was null at CreateGraph time (or the capture was lost on load), LordTick throws
    /// NullReferenceException every tick — while travel/chill toils still run and the caravan can
    /// walk to the colony normally.
    ///
    /// Fix: Prefix the CreateGraph dismiss trigger (&lt;&gt;c__DisplayClass7_0.&lt;CreateGraph&gt;b__0) to skip
    /// the original when trader/mindState is null (treat as "not dismissed"), optionally rebind
    /// trader via FindTrader. RemoveLord only empty orphan trade lords (no ownedPawns). Do not
    /// RemoveLord when FindTrader is null but pawns remain — that would tear down a living caravan
    /// and send it to the map edge.
    ///
    /// Проблема: vanilla LordJob_TradeWithColony.CreateGraph сохраняет FindTrader(lord) в замыкании
    /// Trigger_Custom и читает trader.mindState.traderDismissed без null-check. Если FindTrader был
    /// null при CreateGraph (или ссылка потерялась при load), LordTick каждый тик кидает
    /// NullReferenceException — при этом travel/chill toils работают, и караван может нормально
    /// идти к колонии.
    ///
    /// Исправление: Prefix на dismiss-trigger CreateGraph (&lt;&gt;c__DisplayClass7_0.&lt;CreateGraph&gt;b__0) —
    /// при null trader/mindState пропускать оригинал (считать «не dismissed»), при возможности
    /// перепривязать trader через FindTrader. RemoveLord только для пустых orphan trade-lord (нет
    /// ownedPawns). Не удалять lord при FindTrader == null, если пешки ещё есть — иначе живой
    /// караван развалится и уйдёт к краю карты.
    /// </summary>
    public class TradeCaravanLordFixPatchMod : Mod
    {
        private const string HarmonyId = "kebabebak.trade.caravan.lord.fix.patch";
        private readonly TradeCaravanLordFixPatchSettings settings;

        public TradeCaravanLordFixPatchMod(ModContentPack content)
            : base(content)
        {
            settings = GetSettings<TradeCaravanLordFixPatchSettings>();
            LongEventHandler.ExecuteWhenFinished(ApplyPatches);
        }

        /// <summary>
        /// Registers the dismiss-trigger null guard and empty-orphan LordManagerTick cleanup.
        ///
        /// Подключает null-guard dismiss-trigger и очистку пустых orphan-lord в LordManagerTick.
        /// </summary>
        private static void ApplyPatches()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                MethodBase dismissTrigger = TradeDismissTrigger_Patch.FindTargetMethod();
                if (dismissTrigger == null)
                {
                    Log.Warning(
                        "[TradeCaravanLordFixPatch] CreateGraph dismiss trigger (b__0) not found; " +
                        "NRE guard skipped.");
                }
                else
                {
                    harmony.Patch(
                        dismissTrigger,
                        prefix: new HarmonyMethod(
                            typeof(TradeDismissTrigger_Patch),
                            nameof(TradeDismissTrigger_Patch.Prefix)));
                }

                MethodBase lordTick = AccessTools.Method(typeof(LordManager), nameof(LordManager.LordManagerTick));
                if (lordTick != null)
                {
                    harmony.Patch(
                        lordTick,
                        prefix: new HarmonyMethod(
                            typeof(LordManager_LordManagerTick_Patch),
                            nameof(LordManager_LordManagerTick_Patch.Prefix)));
                }

                Log.Message(
                    $"[TradeCaravanLordFixPatch] Loaded (verbose logging " +
                    $"{(TradeCaravanLordFixPatchSettings.EnableLogging ? "ON" : "OFF")}). " +
                    "Null-safe trade dismiss trigger; empty orphan trade lords only are removed.");
            }
            catch (Exception ex)
            {
                Log.Error("[TradeCaravanLordFixPatch] Failed to apply patches: " + ex);
            }
        }

        public override string SettingsCategory()
        {
            return "TradeCaravanLordFixPatch.SettingsCategory".Translate();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            settings.DrawSettings(inRect);
        }
    }

    /// <summary>
    /// Prefix on the compiler-generated CreateGraph dismiss predicate. return false is required:
    /// the original unconditionally dereferences captured trader.mindState; Postfix cannot prevent
    /// the NRE. Scope is a single boolean trigger used only for the "trader dismissed" transition.
    ///
    /// Prefix на compiler-generated предикат dismiss в CreateGraph. return false обязателен: оригинал
    /// безусловно читает trader.mindState; Postfix не может предотвратить NRE. Область — один
    /// boolean-trigger только для перехода «торговец dismissed».
    /// </summary>
    [HarmonyPriority(Priority.First)]
    internal static class TradeDismissTrigger_Patch
    {
        private static readonly Type DisplayClassType =
            typeof(LordJob_TradeWithColony).GetNestedType("<>c__DisplayClass7_0", BindingFlags.NonPublic);

        private static readonly FieldInfo TraderField =
            DisplayClassType != null
                ? AccessTools.Field(DisplayClassType, "trader")
                : null;

        private static readonly FieldInfo ThisField =
            DisplayClassType != null
                ? AccessTools.Field(DisplayClassType, "<>4__this")
                : null;

        /// <summary>
        /// Resolves &lt;&gt;c__DisplayClass7_0.&lt;CreateGraph&gt;b__0 for Harmony targeting.
        ///
        /// Находит &lt;&gt;c__DisplayClass7_0.&lt;CreateGraph&gt;b__0 для Harmony.
        /// </summary>
        public static MethodBase FindTargetMethod()
        {
            if (DisplayClassType == null)
            {
                return null;
            }

            return AccessTools.Method(DisplayClassType, "<CreateGraph>b__0");
        }

        /// <summary>
        /// Runs vanilla when the captured trader is usable; otherwise tries FindTrader rebind, else
        /// returns false ("not dismissed") without throwing.
        ///
        /// Вызывает vanilla при usable captured trader; иначе пробует перепривязку FindTrader, иначе
        /// возвращает false («не dismissed») без исключения.
        /// </summary>
        public static bool Prefix(object __instance, TriggerSignal s, ref bool __result)
        {
            Pawn trader = TraderField?.GetValue(__instance) as Pawn;
            if (IsUsableTrader(trader))
            {
                return true;
            }

            Pawn repaired = TryFindTrader(__instance);
            if (IsUsableTrader(repaired))
            {
                TraderField?.SetValue(__instance, repaired);
                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Rebound dismiss-trigger trader → {repaired.LabelShort}.");
                return true;
            }

            __result = false;
            PatchLog.Message(
                "[TradeCaravanLordFixPatch] Dismiss trigger skipped (captured trader null/unusable; " +
                "FindTrader also failed). Caravan lord kept.");
            return false;
        }

        /// <summary>
        /// True when the pawn can safely provide mindState.traderDismissed.
        ///
        /// Истина, если у пешки можно безопасно читать mindState.traderDismissed.
        /// </summary>
        private static bool IsUsableTrader(Pawn trader)
        {
            return trader != null && !trader.Destroyed && !trader.Discarded && trader.mindState != null;
        }

        /// <summary>
        /// Looks up the current trader on the outer LordJob_TradeWithColony.lord.
        ///
        /// Ищет текущего торговца через внешний LordJob_TradeWithColony.lord.
        /// </summary>
        private static Pawn TryFindTrader(object displayClass)
        {
            if (ThisField == null || displayClass == null)
            {
                return null;
            }

            var job = ThisField.GetValue(displayClass) as LordJob_TradeWithColony;
            Lord lord = job?.lord;
            if (lord == null)
            {
                return null;
            }

            return TraderCaravanUtility.FindTrader(lord);
        }
    }

    /// <summary>
    /// Prefix on LordManager.LordManagerTick: removes only empty orphan LordJob_TradeWithColony
    /// lords. Must not RemoveLord when FindTrader is null but ownedPawns remain.
    ///
    /// Prefix на LordManager.LordManagerTick: удаляет только пустые orphan LordJob_TradeWithColony.
    /// Нельзя RemoveLord при FindTrader == null, если ownedPawns ещё не пуст.
    /// </summary>
    [HarmonyPatch(typeof(LordManager), nameof(LordManager.LordManagerTick))]
    [HarmonyPriority(Priority.First)]
    internal static class LordManager_LordManagerTick_Patch
    {
        public static void Prefix(LordManager __instance)
        {
            List<Lord> lords = __instance.lords;
            for (int i = lords.Count - 1; i >= 0; i--)
            {
                Lord lord = lords[i];
                if (lord?.LordJob is not LordJob_TradeWithColony)
                {
                    continue;
                }

                if (!lord.ownedPawns.NullOrEmpty())
                {
                    continue;
                }

                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Removing empty orphan trade lord on {lord.Map}.");
                lord.lordManager?.RemoveLord(lord);
            }
        }
    }

    /// <summary>
    /// Optional verbose logging for dismiss-trigger skips, trader rebinds, and orphan removals.
    ///
    /// Опциональный подробный лог пропусков dismiss-trigger, перепривязок торговца и удаления orphan.
    /// </summary>
    public class TradeCaravanLordFixPatchSettings : ModSettings
    {
        public static bool EnableLogging;

        public void DrawSettings(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled(
                "TradeCaravanLordFixPatch.EnableLogging".Translate(),
                ref EnableLogging,
                tooltip: "TradeCaravanLordFixPatch.EnableLoggingTooltip".Translate());
            listing.End();
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref EnableLogging, "EnableLogging", defaultValue: false);
        }
    }

    /// <summary>
    /// Writes Log.Message only when EnableLogging is on.
    ///
    /// Пишет Log.Message только при включённом EnableLogging.
    /// </summary>
    public static class PatchLog
    {
        public static void Message(string text)
        {
            if (TradeCaravanLordFixPatchSettings.EnableLogging)
            {
                Log.Message(text);
            }
        }
    }
}
