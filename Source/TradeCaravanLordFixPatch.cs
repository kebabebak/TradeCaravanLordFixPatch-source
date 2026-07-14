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
    /// walk to the colony normally. A naive "always false when trader is null" guard stops the NRE
    /// but also blocks JobDriver_DismissTrader: that job sets traderDismissed on the traded pawn,
    /// and the leave transition never fires, so colonists keep standing and dismiss stays unavailable.
    ///
    /// Fix: replace the CreateGraph dismiss predicate with a null-safe check. Prefer the captured
    /// trader / FindTrader pawn; if neither works, treat dismiss as true when any owned pawn has
    /// mindState.traderDismissed (the pawn JobDriver_DismissTrader actually flagged). RemoveLord
    /// only empty orphan trade lords (no ownedPawns) — never tear down a living caravan because
    /// FindTrader returned null.
    ///
    /// Проблема: vanilla LordJob_TradeWithColony.CreateGraph сохраняет FindTrader(lord) в замыкании
    /// Trigger_Custom и читает trader.mindState.traderDismissed без null-check. Если FindTrader был
    /// null при CreateGraph (или ссылка потерялась при load), LordTick каждый тик кидает
    /// NullReferenceException — при этом travel/chill toils работают. Наивный guard «всегда false
    /// при null trader» убирает NRE, но ломает JobDriver_DismissTrader: job ставит traderDismissed
    /// на торгуемую пешку, переход «уйти» не срабатывает, караван топчется, отказ недоступен.
    ///
    /// Исправление: заменить dismiss-предикат CreateGraph на null-safe проверку. Сначала captured
    /// trader / FindTrader; если оба недоступны — считать dismiss true, когда у любой owned-пешки
    /// traderDismissed (флаг от JobDriver_DismissTrader). RemoveLord только для пустых orphan
    /// trade-lord — не снимать живой караван из‑за FindTrader == null.
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
    /// Prefix replacement for the compiler-generated CreateGraph dismiss predicate. return false is
    /// required: the original unconditionally dereferences captured trader.mindState. The replacement
    /// mirrors vanilla (Tick signal + traderDismissed) and adds FindTrader rebind + owned-pawn scan
    /// so JobDriver_DismissTrader still starts the leave transition when FindTrader is null.
    ///
    /// Prefix-замена compiler-generated dismiss-предиката CreateGraph. return false обязателен:
    /// оригинал безусловно читает trader.mindState. Замена повторяет vanilla (сигнал Tick +
    /// traderDismissed) и добавляет перепривязку FindTrader + обход owned-пешек, чтобы
    /// JobDriver_DismissTrader по-прежнему запускал уход при FindTrader == null.
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
        /// Null-safe dismiss check for Tick signals. Rebinds/finds trader when possible; otherwise
        /// honors traderDismissed on any lord-owned pawn set by JobDriver_DismissTrader.
        ///
        /// Null-safe проверка dismiss на Tick. Перепривязывает/ищет trader при возможности; иначе
        /// учитывает traderDismissed у любой owned-пешки от JobDriver_DismissTrader.
        /// </summary>
        public static bool Prefix(object __instance, TriggerSignal s, ref bool __result)
        {
            // Vanilla: s.type == TriggerSignalType.Tick && trader.mindState.traderDismissed
            if (s.type != TriggerSignalType.Tick)
            {
                __result = false;
                return false;
            }

            Pawn trader = ResolveTrader(__instance);
            if (IsUsableTrader(trader))
            {
                if (trader.mindState.traderDismissed)
                {
                    __result = true;
                    PatchLog.Message(
                        $"[TradeCaravanLordFixPatch] Dismiss trigger: trader {trader.LabelShort} flagged.");
                    return false;
                }

                // Captured/FindTrader pawn may differ from the pawn the player dismissed.
                if (AnyOwnedPawnTraderDismissed(__instance, out Pawn dismissed))
                {
                    __result = true;
                    PatchLog.Message(
                        $"[TradeCaravanLordFixPatch] Dismiss trigger: owned pawn {dismissed.LabelShort} " +
                        "flagged (not FindTrader role).");
                    return false;
                }

                __result = false;
                return false;
            }

            if (AnyOwnedPawnTraderDismissed(__instance, out Pawn flagged))
            {
                __result = true;
                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Dismiss trigger: owned pawn {flagged.LabelShort} " +
                    "flagged while FindTrader unavailable.");
                return false;
            }

            __result = false;
            return false;
        }

        /// <summary>
        /// Captured trader if usable; otherwise FindTrader result written back into the closure.
        ///
        /// Captured trader если usable; иначе результат FindTrader, записанный обратно в замыкание.
        /// </summary>
        private static Pawn ResolveTrader(object displayClass)
        {
            Pawn trader = TraderField?.GetValue(displayClass) as Pawn;
            if (IsUsableTrader(trader))
            {
                return trader;
            }

            Pawn found = TryFindTrader(displayClass);
            if (IsUsableTrader(found))
            {
                TraderField?.SetValue(displayClass, found);
                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Rebound dismiss-trigger trader → {found.LabelShort}.");
                return found;
            }

            return null;
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
            Lord lord = GetLord(displayClass);
            if (lord == null)
            {
                return null;
            }

            return TraderCaravanUtility.FindTrader(lord);
        }

        /// <summary>
        /// True when any living owned pawn has mindState.traderDismissed (JobDriver_DismissTrader target).
        ///
        /// Истина, если у любой живой owned-пешки traderDismissed (цель JobDriver_DismissTrader).
        /// </summary>
        private static bool AnyOwnedPawnTraderDismissed(object displayClass, out Pawn dismissed)
        {
            dismissed = null;
            Lord lord = GetLord(displayClass);
            List<Pawn> pawns = lord?.ownedPawns;
            if (pawns == null)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (!IsUsableTrader(pawn))
                {
                    continue;
                }

                if (pawn.mindState.traderDismissed)
                {
                    dismissed = pawn;
                    return true;
                }
            }

            return false;
        }

        private static Lord GetLord(object displayClass)
        {
            if (ThisField == null || displayClass == null)
            {
                return null;
            }

            var job = ThisField.GetValue(displayClass) as LordJob_TradeWithColony;
            return job?.lord;
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
    /// Optional verbose logging for dismiss resolution, trader rebinds, and orphan removals.
    ///
    /// Опциональный подробный лог dismiss-разрешения, перепривязок торговца и удаления orphan.
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
