using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
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
    /// Also (narrow): when a TradeWithColony lord has no kindDef.trader pawn but a Guard still carries
    /// GenerateTrader stamps (wantsToTradeWithColony + traderKind), treat that Guard as Trader for
    /// GetTraderCaravanRole/FindTrader. When a kind.trader pawn is present, strip leftover stamps
    /// from non-trader-kind escorts only — never from the sole stamped trader and never Carrier/Chattel.
    ///
    /// Verbose logging (mod settings): dump GenerateTrader makers, GeneratePawn KindDef before/after,
    /// set_KindDef mutations, CreateGraph lord composition, ChangeKind, and periodic FindTrader-null
    /// lord dumps — for diagnosing missing Tribal_Trader / wrong combat kind with wantsToTradeWithColony.
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
    ///
    /// Также (узко): если в TradeWithColony lord нет пешки с kindDef.trader, но у Guard остались
    /// штампы GenerateTrader (wantsToTradeWithColony + traderKind) — считать его Trader для
    /// GetTraderCaravanRole/FindTrader. Если kind.trader в lord есть — снимать leftover-штампы
    /// только с escorts без kind.trader; единственного stamped-торговца и Carrier/Chattel не трогать.
    ///
    /// Подробный лог (настройки мода): GenerateTrader makers, KindDef до/после GeneratePawn,
    /// мутации set_KindDef, состав lord в CreateGraph, ChangeKind и периодические дампы lord без
    /// FindTrader — для диагностики отсутствия Tribal_Trader / боевого kind с wantsToTradeWithColony.
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
        /// Registers dismiss-trigger null guard, orphan cleanup, narrow FindTrader role fix,
        /// leftover trader-stamp hygiene, and diagnostic hooks.
        ///
        /// Подключает null-guard dismiss-trigger, cleanup orphan-lord, узкий FindTrader role-fix,
        /// гигиену leftover trader-штампов и диагностические хуки.
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

                MethodBase getRole = AccessTools.Method(
                    typeof(TraderCaravanUtility),
                    nameof(TraderCaravanUtility.GetTraderCaravanRole));
                if (getRole != null)
                {
                    HarmonyMethod rolePost = new HarmonyMethod(
                        typeof(GetTraderCaravanRole_Patch),
                        nameof(GetTraderCaravanRole_Patch.Postfix))
                    {
                        // Run after HAR's GetTraderCaravanRole transpiler/infixes when present.
                        after = new[] { "erdelf.HumanoidAlienRaces" },
                        priority = Priority.Last
                    };
                    harmony.Patch(getRole, postfix: rolePost);
                }
                else
                {
                    Log.Warning("[TradeCaravanLordFixPatch] GetTraderCaravanRole not found; role fix skipped.");
                }

                MethodBase createGraph = AccessTools.Method(
                    typeof(LordJob_TradeWithColony),
                    nameof(LordJob_TradeWithColony.CreateGraph));
                if (createGraph != null)
                {
                    harmony.Patch(
                        createGraph,
                        postfix: new HarmonyMethod(
                            typeof(LordJob_TradeWithColony_CreateGraph_Fix),
                            nameof(LordJob_TradeWithColony_CreateGraph_Fix.Postfix)));
                }

                try
                {
                    TradeDiagPatches.Apply(harmony);
                }
                catch (Exception diagEx)
                {
                    Log.Error("[TradeCaravanLordFixPatch] Diagnostic patches failed (fix still active): " + diagEx);
                }

                Log.Message(
                    $"[TradeCaravanLordFixPatch] Loaded (verbose logging " +
                    $"{(TradeCaravanLordFixPatchSettings.EnableLogging ? "ON" : "OFF")}). " +
                    "Null-safe dismiss; empty orphan cleanup; narrow Guard→Trader role when no " +
                    "kind.trader in lord; leftover stamp hygiene when kind.trader present.");
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

            TradeDiag.NoteMissingFindTraderOnDismissTick(GetLord(__instance));
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
    /// Shared helpers for narrow FindTrader role elevation and leftover trader-stamp hygiene.
    ///
    /// Общие хелперы узкого поднятия роли FindTrader и гигиены leftover trader-штампов.
    /// </summary>
    internal static class TradeLordRoleFix
    {
        /// <summary>
        /// True when any owned living pawn has kindDef.trader (vanilla FindTrader target).
        ///
        /// Истина, если у любой живой owned-пешки kindDef.trader (ванильная цель FindTrader).
        /// </summary>
        public static bool LordHasKindTrader(Lord lord)
        {
            List<Pawn> pawns = lord?.ownedPawns;
            if (pawns == null)
            {
                return false;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (!IsLivingOwned(pawn))
                {
                    continue;
                }

                if (pawn.kindDef != null && pawn.kindDef.trader)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// GenerateTrader-style stamps: wantsToTradeWithColony and a traderKind on the tracker.
        ///
        /// Штампы GenerateTrader: wantsToTradeWithColony и traderKind на tracker.
        /// </summary>
        public static bool HasFullTraderStamps(Pawn pawn)
        {
            if (pawn?.mindState == null || !pawn.mindState.wantsToTradeWithColony)
            {
                return false;
            }

            return pawn.trader != null && pawn.trader.traderKind != null;
        }

        /// <summary>
        /// Partial leftover stamps worth clearing when a kind.trader already leads the lord.
        ///
        /// Частичные leftover-штампы, которые стоит снять, если kind.trader уже ведёт lord.
        /// </summary>
        public static bool HasAnyTraderStamp(Pawn pawn)
        {
            if (pawn?.mindState != null && pawn.mindState.wantsToTradeWithColony)
            {
                return true;
            }

            return pawn?.trader != null && pawn.trader.traderKind != null;
        }

        /// <summary>
        /// Narrow elevation gate: Guard in TradeWithColony lord, full stamps, no kind.trader in lord.
        ///
        /// Узкое условие поднятия: Guard в TradeWithColony, полные штампы, в lord нет kind.trader.
        /// </summary>
        public static bool ShouldElevateGuardToTrader(Pawn pawn, TraderCaravanRole currentRole)
        {
            if (currentRole != TraderCaravanRole.Guard)
            {
                return false;
            }

            if (!IsLivingOwned(pawn) || !HasFullTraderStamps(pawn))
            {
                return false;
            }

            // Never elevate real kind traders (already Trader from vanilla) or animals/etc.
            if (pawn.kindDef != null && pawn.kindDef.trader)
            {
                return false;
            }

            if (pawn.RaceProps != null && pawn.RaceProps.Animal)
            {
                return false;
            }

            Lord lord = pawn.GetLord();
            if (lord?.LordJob is not LordJob_TradeWithColony)
            {
                return false;
            }

            // Critical safety: if a kind.trader exists, leave this Guard as Guard (no dual-Trader).
            return !LordHasKindTrader(lord);
        }

        /// <summary>
        /// Strip leftover wantsTrade/traderKind from non-kind.trader escorts when a kind.trader is present.
        /// Does not touch the sole stamped trader of a kind.trader-less lord (broken Toxo-style case).
        ///
        /// Снимает leftover wantsTrade/traderKind с escorts без kind.trader, если kind.trader уже есть.
        /// Не трогает единственного stamped-торговца lord без kind.trader (кейс Toxo).
        /// </summary>
        public static void ClearLeftoverTraderStamps(Lord lord)
        {
            if (lord?.LordJob is not LordJob_TradeWithColony)
            {
                return;
            }

            List<Pawn> pawns = lord.ownedPawns;
            if (pawns.NullOrEmpty() || !LordHasKindTrader(lord))
            {
                return;
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (!IsLivingOwned(pawn))
                {
                    continue;
                }

                if (pawn.kindDef != null && pawn.kindDef.trader)
                {
                    continue;
                }

                if (!HasAnyTraderStamp(pawn))
                {
                    continue;
                }

                ClearTraderStamps(pawn);
                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Cleared leftover trader stamps on escort " +
                    $"{pawn.LabelShort}/{pawn.ThingID} (kind.trader already in lord).");
            }
        }

        private static void ClearTraderStamps(Pawn pawn)
        {
            if (pawn.mindState != null && pawn.mindState.wantsToTradeWithColony)
            {
                pawn.mindState.wantsToTradeWithColony = false;
            }

            if (pawn.trader != null && pawn.trader.traderKind != null)
            {
                pawn.trader.traderKind = null;
            }
        }

        private static bool IsLivingOwned(Pawn pawn)
        {
            return pawn != null && !pawn.Destroyed && !pawn.Discarded && !pawn.Dead;
        }
    }

    /// <summary>
    /// Narrow Postfix: Guard + full GenerateTrader stamps + TradeWithColony lord without kind.trader
    /// → Trader. Never elevates Carrier/Chattel; never elevates when a kind.trader already exists.
    ///
    /// Узкий Postfix: Guard + полные штампы GenerateTrader + TradeWithColony без kind.trader
    /// → Trader. Не поднимает Carrier/Chattel; не поднимает, если kind.trader уже есть.
    /// </summary>
    internal static class GetTraderCaravanRole_Patch
    {
        private static readonly HashSet<int> LoggedElevateThingIds = new HashSet<int>();

        public static void Postfix(Pawn p, ref TraderCaravanRole __result)
        {
            if (!TradeLordRoleFix.ShouldElevateGuardToTrader(p, __result))
            {
                return;
            }

            __result = TraderCaravanRole.Trader;

            // Role is queried every tick; log once per pawn id when verbose logging is on.
            if (TradeCaravanLordFixPatchSettings.EnableLogging &&
                p != null &&
                LoggedElevateThingIds.Add(p.thingIDNumber))
            {
                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Elevated Guard→Trader for FindTrader: " +
                    $"{p.LabelShort}/{p.ThingID} kind={p.kindDef?.defName} " +
                    "(no kind.trader in lord; full trader stamps present).");
            }
        }
    }

    /// <summary>
    /// After CreateGraph builds the trade lord: strip escort leftover stamps when kind.trader exists.
    /// Runs after FindTrader capture inside CreateGraph (healthy caravans already used kind.trader).
    ///
    /// После CreateGraph: снять leftover-штампы escorts, если kind.trader есть.
    /// Выполняется после захвата FindTrader внутри CreateGraph (healthy уже взял kind.trader).
    /// </summary>
    internal static class LordJob_TradeWithColony_CreateGraph_Fix
    {
        public static void Postfix(LordJob_TradeWithColony __instance)
        {
            TradeLordRoleFix.ClearLeftoverTraderStamps(__instance?.lord);
        }
    }

    /// <summary>
    /// Prefix on LordManager.LordManagerTick: removes only empty orphan LordJob_TradeWithColony
    /// lords. Must not RemoveLord when FindTrader is null but ownedPawns remain. Also leftover
    /// trader-stamp hygiene and periodic FindTrader-null diagnostic dumps.
    ///
    /// Prefix на LordManager.LordManagerTick: удаляет только пустые orphan LordJob_TradeWithColony.
    /// Нельзя RemoveLord при FindTrader == null, если ownedPawns ещё не пуст. Также гигиена
    /// leftover trader-штампов и периодические диагностические дампы FindTrader==null.
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
                    TradeLordRoleFix.ClearLeftoverTraderStamps(lord);
                    TradeDiag.MaybeDumpTradeLordMissingTrader(lord);
                    continue;
                }

                PatchLog.Message(
                    $"[TradeCaravanLordFixPatch] Removing empty orphan trade lord on {lord.Map}.");
                lord.lordManager?.RemoveLord(lord);
            }
        }
    }

    /// <summary>
    /// Registers diagnostic Harmony patches used only for Player.log investigation output.
    ///
    /// Регистрирует диагностические Harmony-патчи для вывода в Player.log.
    /// </summary>
    internal static class TradeDiagPatches
    {
        public static void Apply(Harmony harmony)
        {
            // GenerateTrader is private on PawnGroupKindWorker_Trader.
            MethodBase generateTrader = AccessTools.Method(typeof(PawnGroupKindWorker_Trader), "GenerateTrader");
            if (generateTrader != null)
            {
                harmony.Patch(
                    generateTrader,
                    prefix: new HarmonyMethod(typeof(Diag_GenerateTrader), nameof(Diag_GenerateTrader.Prefix)),
                    postfix: new HarmonyMethod(typeof(Diag_GenerateTrader), nameof(Diag_GenerateTrader.Postfix)));
            }
            else
            {
                Log.Warning("[TradeCaravanLordFixPatch] GenerateTrader not found; trader-spawn diag skipped.");
            }

            harmony.Patch(
                AccessTools.Method(
                    typeof(PawnGenerator),
                    nameof(PawnGenerator.GeneratePawn),
                    new[] { typeof(PawnGenerationRequest) }),
                prefix: new HarmonyMethod(typeof(Diag_GeneratePawn), nameof(Diag_GeneratePawn.Prefix)),
                postfix: new HarmonyMethod(typeof(Diag_GeneratePawn), nameof(Diag_GeneratePawn.Postfix)));

            MethodBase setKind = AccessTools.PropertySetter(typeof(PawnGenerationRequest), nameof(PawnGenerationRequest.KindDef));
            if (setKind != null)
            {
                harmony.Patch(
                    setKind,
                    prefix: new HarmonyMethod(typeof(Diag_SetKindDef), nameof(Diag_SetKindDef.Prefix)));
            }

            harmony.Patch(
                AccessTools.Method(typeof(LordJob_TradeWithColony), nameof(LordJob_TradeWithColony.CreateGraph)),
                postfix: new HarmonyMethod(typeof(Diag_CreateGraph), nameof(Diag_CreateGraph.Postfix)));

            harmony.Patch(
                AccessTools.Method(typeof(Pawn), nameof(Pawn.ChangeKind)),
                prefix: new HarmonyMethod(typeof(Diag_ChangeKind), nameof(Diag_ChangeKind.Prefix)));

            PatchLog.Message("[TradeCaravanLordFixPatch] Diagnostic patches registered.");
        }
    }

    /// <summary>
    /// Logs PawnGroupMaker traders/carriers/guards and the spawned trader pawn from GenerateTrader.
    ///
    /// Логирует traders/carriers/guards PawnGroupMaker и пешку торговца из GenerateTrader.
    /// </summary>
    internal static class Diag_GenerateTrader
    {
        // Vanilla parameter names: parms, groupMaker, traderKind (Harmony binds by name).
        public static void Prefix(PawnGroupMakerParms parms, PawnGroupMaker groupMaker, TraderKindDef traderKind)
        {
            TradeDiag.EnterGenerateTrader();
            if (!TradeCaravanLordFixPatchSettings.EnableLogging)
            {
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[TradeCaravanLordFixPatch] GenerateTrader BEGIN");
            sb.AppendLine(TradeDiag.FormatParms(parms, traderKind));
            sb.AppendLine(TradeDiag.FormatMaker(groupMaker));
            if (parms?.faction?.def != null)
            {
                sb.AppendLine(TradeDiag.FormatFactionTraderMakers(parms.faction.def));
            }

            PatchLog.Message(sb.ToString().TrimEnd());
        }

        public static void Postfix(Pawn __result)
        {
            try
            {
                if (TradeCaravanLordFixPatchSettings.EnableLogging)
                {
                    PatchLog.Message(
                        "[TradeCaravanLordFixPatch] GenerateTrader END → " + TradeDiag.FormatPawn(__result));
                }
            }
            finally
            {
                TradeDiag.ExitGenerateTrader();
            }
        }
    }

    /// <summary>
    /// Logs KindDef before/after GeneratePawn when generating a trader kind or inside GenerateTrader.
    ///
    /// Логирует KindDef до/после GeneratePawn для trader-kind или внутри GenerateTrader.
    /// </summary>
    internal static class Diag_GeneratePawn
    {
        public static void Prefix(ref PawnGenerationRequest request, ref PawnKindDef __state)
        {
            __state = null;
            if (!TradeCaravanLordFixPatchSettings.EnableLogging)
            {
                return;
            }

            PawnKindDef kind = request.KindDef;
            if (!TradeDiag.ShouldTrackGeneratePawn(kind))
            {
                return;
            }

            __state = kind;
            TradeDiag.EnterGeneratePawn(kind);
            PatchLog.Message(
                "[TradeCaravanLordFixPatch] GeneratePawn BEGIN " +
                $"kind={TradeDiag.FormatKind(kind)} faction={request.Faction?.Name ?? "null"} " +
                $"context={request.Context} colonist={request.Faction?.IsPlayer.ToString() ?? "?"} " +
                $"forcedXeno={request.ForcedXenotype?.defName ?? "null"} " +
                $"inGenerateTrader={TradeDiag.InGenerateTrader}");
        }

        public static void Postfix(PawnGenerationRequest request, Pawn __result, PawnKindDef __state)
        {
            if (__state == null)
            {
                return;
            }

            PawnKindDef requestNow = request.KindDef;
            PawnKindDef resultKind = __result?.kindDef;
            bool requestMutated = requestNow != __state;
            bool resultDiffers = resultKind != null && resultKind != __state;

            StringBuilder sb = new StringBuilder();
            sb.Append("[TradeCaravanLordFixPatch] GeneratePawn END ");
            sb.Append($"requestBefore={TradeDiag.FormatKind(__state)} ");
            sb.Append($"requestAfter={TradeDiag.FormatKind(requestNow)} ");
            sb.Append($"result={TradeDiag.FormatPawn(__result)}");
            if (requestMutated || resultDiffers)
            {
                sb.Append(" *** KIND MISMATCH / MUTATION ***");
            }

            PatchLog.Message(sb.ToString());
            TradeDiag.ExitGeneratePawn();
        }
    }

    /// <summary>
    /// Logs every KindDef rewrite on PawnGenerationRequest while a tracked GeneratePawn is running.
    ///
    /// Логирует каждую перезапись KindDef на PawnGenerationRequest во время tracked GeneratePawn.
    /// </summary>
    internal static class Diag_SetKindDef
    {
        public static void Prefix(PawnGenerationRequest __instance, PawnKindDef value)
        {
            if (!TradeCaravanLordFixPatchSettings.EnableLogging || !TradeDiag.InGeneratePawn)
            {
                return;
            }

            PawnKindDef before = __instance.KindDef;
            if (before == value)
            {
                return;
            }

            PatchLog.Message(
                "[TradeCaravanLordFixPatch] set_KindDef " +
                $"{TradeDiag.FormatKind(before)} → {TradeDiag.FormatKind(value)} " +
                $"(inGenerateTrader={TradeDiag.InGenerateTrader}) " +
                $"stack={TradeDiag.TrimStack(Environment.StackTrace)}");
        }
    }

    /// <summary>
    /// Dumps FindTrader + full owned pawn table right after CreateGraph builds the lord state graph.
    ///
    /// Дамп FindTrader и таблицы owned-пешек сразу после CreateGraph.
    /// </summary>
    internal static class Diag_CreateGraph
    {
        public static void Postfix(LordJob_TradeWithColony __instance)
        {
            if (!TradeCaravanLordFixPatchSettings.EnableLogging)
            {
                return;
            }

            Lord lord = __instance?.lord;
            PatchLog.Message(
                "[TradeCaravanLordFixPatch] CreateGraph DONE\n" + TradeDiag.FormatTradeLord(lord));
        }
    }

    /// <summary>
    /// Logs Pawn.ChangeKind attempts (HAR often blocks humanlike→humanlike ChangeKind).
    ///
    /// Логирует попытки Pawn.ChangeKind (HAR часто блокирует humanlike→humanlike).
    /// </summary>
    internal static class Diag_ChangeKind
    {
        public static void Prefix(Pawn __instance, PawnKindDef newKindDef)
        {
            if (!TradeCaravanLordFixPatchSettings.EnableLogging)
            {
                return;
            }

            PawnKindDef before = __instance?.kindDef;
            bool relevant =
                TradeDiag.InGenerateTrader ||
                TradeDiag.KindLooksTraderRelated(before) ||
                TradeDiag.KindLooksTraderRelated(newKindDef) ||
                (__instance?.mindState != null && __instance.mindState.wantsToTradeWithColony);

            if (!relevant)
            {
                return;
            }

            PatchLog.Message(
                "[TradeCaravanLordFixPatch] ChangeKind " +
                $"{__instance?.LabelShortCap ?? "null"} " +
                $"{TradeDiag.FormatKind(before)} → {TradeDiag.FormatKind(newKindDef)} " +
                $"stack={TradeDiag.TrimStack(Environment.StackTrace)}");
        }
    }

    /// <summary>
    /// Shared formatting and rate-limited dumps for trade-caravan diagnostics.
    ///
    /// Общие форматтеры и rate-limited дампы диагностики торговых караванов.
    /// </summary>
    internal static class TradeDiag
    {
        private const int MissingTraderDumpIntervalTicks = 2500;

        [ThreadStatic]
        private static int generateTraderDepth;

        [ThreadStatic]
        private static int generatePawnDepth;

        private static readonly HashSet<int> DumpedMissingFindTraderLords = new HashSet<int>();
        private static readonly Dictionary<int, int> LastMissingDumpTickByLord = new Dictionary<int, int>();

        private static FieldInfo alienOriginalKindField;
        private static bool alienOriginalKindResolved;

        public static bool InGenerateTrader => generateTraderDepth > 0;
        public static bool InGeneratePawn => generatePawnDepth > 0;

        public static void EnterGenerateTrader() => generateTraderDepth++;
        public static void ExitGenerateTrader()
        {
            if (generateTraderDepth > 0)
            {
                generateTraderDepth--;
            }
        }

        public static void EnterGeneratePawn(PawnKindDef _) => generatePawnDepth++;
        public static void ExitGeneratePawn()
        {
            if (generatePawnDepth > 0)
            {
                generatePawnDepth--;
            }
        }

        public static bool ShouldTrackGeneratePawn(PawnKindDef kind)
        {
            return InGenerateTrader || KindLooksTraderRelated(kind);
        }

        public static bool KindLooksTraderRelated(PawnKindDef kind)
        {
            if (kind == null)
            {
                return false;
            }

            if (kind.trader)
            {
                return true;
            }

            string name = kind.defName ?? string.Empty;
            return name.IndexOf("Trader", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static void NoteMissingFindTraderOnDismissTick(Lord lord)
        {
            MaybeDumpTradeLordMissingTrader(lord, forceFirst: true);
        }

        public static void MaybeDumpTradeLordMissingTrader(Lord lord, bool forceFirst = false)
        {
            if (!TradeCaravanLordFixPatchSettings.EnableLogging || lord == null)
            {
                return;
            }

            if (lord.LordJob is not LordJob_TradeWithColony)
            {
                return;
            }

            if (lord.ownedPawns.NullOrEmpty())
            {
                return;
            }

            if (TraderCaravanUtility.FindTrader(lord) != null)
            {
                return;
            }

            int id = lord.GetUniqueLoadID().GetHashCode();
            int now = Find.TickManager?.TicksGame ?? 0;
            bool first = DumpedMissingFindTraderLords.Add(id);
            if (!first && !forceFirst)
            {
                if (LastMissingDumpTickByLord.TryGetValue(id, out int last) &&
                    now - last < MissingTraderDumpIntervalTicks)
                {
                    return;
                }
            }

            LastMissingDumpTickByLord[id] = now;
            PatchLog.Message(
                "[TradeCaravanLordFixPatch] Trade lord with FindTrader==null " +
                $"(first={first} tick={now})\n{FormatTradeLord(lord)}");
        }

        public static string FormatTradeLord(Lord lord)
        {
            if (lord == null)
            {
                return "lord=null";
            }

            Pawn found = TraderCaravanUtility.FindTrader(lord);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"lord={lord.GetUniqueLoadID()} map={lord.Map} job={lord.LordJob?.GetType().Name}");
            sb.AppendLine($"FindTrader={(found == null ? "NULL" : FormatPawn(found))}");
            sb.AppendLine($"ownedPawns={lord.ownedPawns?.Count ?? 0}");

            List<Pawn> pawns = lord.ownedPawns;
            if (pawns == null)
            {
                return sb.ToString().TrimEnd();
            }

            for (int i = 0; i < pawns.Count; i++)
            {
                sb.AppendLine($"  [{i}] {FormatPawn(pawns[i])}");
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatPawn(Pawn pawn)
        {
            if (pawn == null)
            {
                return "pawn=null";
            }

            PawnKindDef kind = pawn.kindDef;
            var mind = pawn.mindState;
            string traderKind = pawn.trader?.traderKind?.defName ?? "null";
            TraderCaravanRole role = default;
            string roleText = "n/a";
            try
            {
                role = TraderCaravanUtility.GetTraderCaravanRole(pawn);
                roleText = role.ToString();
            }
            catch (Exception ex)
            {
                roleText = "ERR:" + ex.GetType().Name;
            }

            bool wants = mind != null && mind.wantsToTradeWithColony;
            bool dismissed = mind != null && mind.traderDismissed;
            string original = TryAlienOriginalKind(pawn);

            return
                $"{pawn.LabelShortCap}/{pawn.ThingID} " +
                $"kind={FormatKind(kind)} role={roleText} " +
                $"wantsTrade={wants} dismissed={dismissed} traderKindDef={traderKind} " +
                $"race={pawn.def?.defName ?? "null"} faction={pawn.Faction?.Name ?? "null"} " +
                $"xenotype={pawn.genes?.Xenotype?.defName ?? "null"} " +
                $"alienOriginalKind={original} " +
                $"dead={pawn.Dead} destroyed={pawn.Destroyed} mindNull={mind == null}";
        }

        public static string FormatKind(PawnKindDef kind)
        {
            if (kind == null)
            {
                return "null";
            }

            return $"{kind.defName}(trader={kind.trader})";
        }

        public static string FormatParms(PawnGroupMakerParms parms, TraderKindDef traderKind)
        {
            if (parms == null)
            {
                return "parms=null";
            }

            return
                $"faction={parms.faction?.Name ?? "null"}/{parms.faction?.def?.defName ?? "null"} " +
                $"groupKind={parms.groupKind?.defName ?? "null"} " +
                $"traderKind={traderKind?.defName ?? parms.traderKind?.defName ?? "null"} " +
                $"points={parms.points} tile={parms.tile} ideo={parms.ideo?.name ?? "null"}";
        }

        public static string FormatMaker(PawnGroupMaker maker)
        {
            if (maker == null)
            {
                return "maker=null";
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"maker.kindDef={maker.kindDef}");
            sb.AppendLine("  traders=" + FormatKindCountList(maker.traders));
            sb.AppendLine("  carriers=" + FormatKindCountList(maker.carriers));
            sb.AppendLine("  guards=" + FormatKindCountList(maker.guards));
            sb.Append("  options=" + FormatKindCountList(maker.options));
            return sb.ToString().TrimEnd();
        }

        public static string FormatFactionTraderMakers(FactionDef factionDef)
        {
            if (factionDef?.pawnGroupMakers == null)
            {
                return "faction.pawnGroupMakers=null";
            }

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"factionDef={factionDef.defName} pawnGroupMakers:");
            for (int i = 0; i < factionDef.pawnGroupMakers.Count; i++)
            {
                PawnGroupMaker maker = factionDef.pawnGroupMakers[i];
                if (maker == null)
                {
                    continue;
                }

                bool traderish =
                    maker.kindDef == PawnGroupKindDefOf.Trader ||
                    (maker.traders != null && maker.traders.Count > 0);
                if (!traderish)
                {
                    continue;
                }

                sb.AppendLine($"  [{i}] kindDef={maker.kindDef}");
                sb.AppendLine("    traders=" + FormatKindCountList(maker.traders));
                sb.AppendLine("    carriers=" + FormatKindCountList(maker.carriers));
                sb.AppendLine("    guards=" + FormatKindCountList(maker.guards));
                sb.AppendLine("    options=" + FormatKindCountList(maker.options));
            }

            return sb.ToString().TrimEnd();
        }

        public static string FormatKindCountList(List<PawnGenOption> list)
        {
            if (list == null || list.Count == 0)
            {
                return "(empty)";
            }

            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < list.Count; i++)
            {
                PawnGenOption opt = list[i];
                if (i > 0)
                {
                    sb.Append(", ");
                }

                if (opt == null)
                {
                    sb.Append("null");
                    continue;
                }

                sb.Append(FormatKind(opt.kind));
                sb.Append('*');
                sb.Append(opt.selectionWeight);
            }

            return sb.ToString();
        }

        public static string TrimStack(string stack)
        {
            if (string.IsNullOrEmpty(stack))
            {
                return "";
            }

            string[] lines = stack.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            int take = Math.Min(12, lines.Length);
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < take; i++)
            {
                string line = lines[i].Trim();
                if (line.IndexOf("HarmonyLib", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("TradeCaravanLordFixPatch", StringComparison.Ordinal) >= 0 ||
                    line.IndexOf("System.Environment", StringComparison.Ordinal) >= 0)
                {
                    continue;
                }

                if (sb.Length > 0)
                {
                    sb.Append(" | ");
                }

                sb.Append(line);
                if (sb.Length > 500)
                {
                    sb.Append(" …");
                    break;
                }
            }

            return sb.ToString();
        }

        private static string TryAlienOriginalKind(Pawn pawn)
        {
            if (pawn == null)
            {
                return "n/a";
            }

            try
            {
                ThingComp alien = null;
                if (pawn.AllComps != null)
                {
                    for (int i = 0; i < pawn.AllComps.Count; i++)
                    {
                        ThingComp c = pawn.AllComps[i];
                        if (c == null)
                        {
                            continue;
                        }

                        string tn = c.GetType().FullName ?? c.GetType().Name;
                        if (tn.IndexOf("AlienComp", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            alien = c;
                            break;
                        }
                    }
                }

                if (alien == null)
                {
                    return "none";
                }

                if (!alienOriginalKindResolved)
                {
                    alienOriginalKindResolved = true;
                    alienOriginalKindField =
                        AccessTools.Field(alien.GetType(), "originalKindDef") ??
                        AccessTools.Field(alien.GetType(), "OriginalKindDef");
                }

                if (alienOriginalKindField == null)
                {
                    return "comp-no-field";
                }

                object val = alienOriginalKindField.GetValue(alien);
                return FormatKind(val as PawnKindDef);
            }
            catch (Exception ex)
            {
                return "ERR:" + ex.GetType().Name;
            }
        }
    }

    /// <summary>
    /// Optional verbose logging for dismiss resolution and root-cause diagnostics.
    ///
    /// Опциональный подробный лог dismiss-разрешения и диагностики корневой причины.
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
