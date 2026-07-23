using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace Ramazon
{
    /// <summary>
    /// Precisao da rede do Ramazon. Sem um colono portando o archolink, o portal
    /// opera "cego": precos inflacionados, itens sem cotacao e entregas trocadas
    /// por produtos mais baratos. Com o archolink, tudo opera normal.
    /// </summary>
    public static class RamazonAccuracy
    {
        /// <summary>Multiplicador extra de preco quando NAO ha archolink.</summary>
        public const float ImpreciseMarkup = 3f;

        /// <summary>Chance de cada item entregue vir trocado (so quando impreciso).</summary>
        public const float WrongItemChance = 0.25f;

        /// <summary>Fracao do catalogo que aparece sem preco (so quando impreciso).</summary>
        public const float UnknownPriceChance = 0.40f;

        public const string HediffDefName = "Ramazon_ArcholinkInstalled";

        /// <summary>Teto de valor por item que a rede alcanca sem archolink.</summary>
        public const float MaxValueWithoutLink = 800f;

        /// <summary>
        /// Sem archolink a rede so alcanca mercadoria simples: nada de implantes,
        /// nada de ultra/arcotech e nada acima do teto de valor.
        /// O proprio archolink e isento (senao seria impossivel adquirir o primeiro).
        /// </summary>
        public static bool IsRestricted(Map map, ThingDef def, out string reason)
        {
            reason = null;
            if (def == null) return false;
            if (HasLink(map)) return false;

            // O archolink e isento das restricoes de proposito: o portal ja e o gate
            // dificil (exige nucleo de personalidade). Se o archolink tambem dependesse
            // de sorte (missao), uma partida inteira poderia ficar sem precisao nenhuma.
            // Assim ha sempre um caminho deterministico: juntar prata e compra-lo.
            if (def.defName == "Ramazon_Archolink") return false;

            if (def.isTechHediff)
            {
                reason = "Sem archolink a rede nao consegue materializar implantes e proteses.";
                return true;
            }

            if (def.techLevel >= TechLevel.Ultra)
            {
                reason = "Sem archolink a rede nao alcanca mercadoria ultra/arcotech.";
                return true;
            }

            if (def.BaseMarketValue > MaxValueWithoutLink)
            {
                reason = "Sem archolink a rede so entrega itens ate " + MaxValueWithoutLink.ToString("0") + " de valor.";
                return true;
            }

            return false;
        }

        /// <summary>Existe um colono com archolink neste mapa?</summary>
        public static bool HasLink(Map map)
        {
            if (map == null) return false;
            var hdef = DefDatabase<HediffDef>.GetNamedSilentFail(HediffDefName);
            if (hdef == null) return false;

            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p != null && !p.Dead && p.health?.hediffSet != null
                    && p.health.hediffSet.HasHediff(hdef))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// O colono que esta "negociando": o portador do archolink. Se houver mais de um,
        /// vale o melhor negociador. Sem archolink nao ha negociador - a rede fala sozinha.
        /// </summary>
        public static Pawn Negotiator(Map map)
        {
            if (map == null) return null;
            var hdef = DefDatabase<HediffDef>.GetNamedSilentFail(HediffDefName);
            if (hdef == null) return null;

            Pawn best = null;
            float bestVal = float.MinValue;

            var colonists = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < colonists.Count; i++)
            {
                var p = colonists[i];
                if (p == null || p.Dead || p.health?.hediffSet == null) continue;
                if (!p.health.hediffSet.HasHediff(hdef)) continue;

                float v = p.GetStatValue(StatDefOf.TradePriceImprovement);
                if (v > bestVal) { bestVal = v; best = p; }
            }
            return best;
        }

        /// <summary>
        /// Melhoria de preco do negociador (mesma estatistica das caravanas).
        /// 0 quando nao ha archolink instalado.
        /// </summary>
        public static float PriceImprovement(Map map)
        {
            var n = Negotiator(map);
            if (n == null) return 0f;
            return Mathf.Clamp(n.GetStatValue(StatDefOf.TradePriceImprovement), 0f, 0.9f);
        }

        /// <summary>Multiplicador de preco efetivo: agio da imprecisao ou desconto do negociador.</summary>
        public static float EffectiveMultiplier(Map map, RamazonSettings st)
        {
            float baseMult = st != null ? st.priceMultiplier : 1f;

            if (!HasLink(map)) return baseMult * ImpreciseMarkup;

            // Com archolink, o portador negocia: paga menos.
            return baseMult * (1f - PriceImprovement(map));
        }

        /// <summary>
        /// Fracao do valor recebida na venda (taxa do mod + bonus do negociador).
        /// </summary>
        public static float SellReceiveFactor(Map map, RamazonSettings st)
        {
            float baseFactor = st != null ? (100f - st.sellTaxPercent) / 100f : 1f;
            return Mathf.Clamp(baseFactor * (1f + PriceImprovement(map)), 0f, 1.5f);
        }

        /// <summary>Texto curto do negociador pra UI (null se nao houver).</summary>
        public static string NegotiatorLabel(Map map)
        {
            var n = Negotiator(map);
            if (n == null) return null;
            return "Negociador: " + n.LabelShort + " (" + (PriceImprovement(map) * 100f).ToString("0") + "%)";
        }

        /// <summary>
        /// Este item aparece sem cotacao? Estavel por item (hash do defName + semente
        /// do mundo), pra nao ficar piscando a cada frame.
        /// </summary>
        public static bool PriceIsHidden(Map map, ThingDef def)
        {
            if (def == null) return false;
            if (HasLink(map)) return false;

            int seed = 0;
            var world = Find.World;
            if (world != null && world.info != null) seed = world.info.persistentRandomValue;

            uint h = (uint)(def.defName.GetHashCode() ^ seed);
            // mistura simples pra espalhar bem
            h ^= h >> 13; h *= 0x5bd1e995; h ^= h >> 15;
            return (h % 1000u) < (uint)(UnknownPriceChance * 1000f);
        }

        // Catalogo de itens baratos usado nas entregas trocadas.
        private static List<ThingDef> cheapPool;

        private static List<ThingDef> CheapPool
        {
            get
            {
                if (cheapPool == null)
                {
                    cheapPool = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(d => d.category == ThingCategory.Item
                                 && !d.MadeFromStuff
                                 && !d.IsCorpse
                                 && d.BaseMarketValue > 0.5f
                                 && d.BaseMarketValue < 60f
                                 && d.tradeability != Tradeability.None)
                        .ToList();
                }
                return cheapPool;
            }
        }

        /// <summary>
        /// Aplica a imprecisao na lista a ser entregue: parte dos itens vira outro
        /// produto SEMPRE de menor valor. Devolve quantos foram trocados.
        /// </summary>
        public static int Garble(Map map, List<Thing> things)
        {
            if (things == null || things.Count == 0) return 0;
            if (HasLink(map)) return 0;
            if (CheapPool.Count == 0) return 0;

            int swapped = 0;
            for (int i = 0; i < things.Count; i++)
            {
                var t = things[i];
                if (t == null || t is Pawn) continue;              // animais nao sao trocados
                if (!Rand.Chance(WrongItemChance)) continue;

                float unitValue = t.def.BaseMarketValue;
                if (unitValue <= 1f) continue;

                // so candidatos mais baratos que o item original
                var cheaper = CheapPool.Where(d => d.BaseMarketValue < unitValue * 0.6f).ToList();
                if (cheaper.Count == 0) continue;

                var pick = cheaper.RandomElement();
                var replacement = ThingMaker.MakeThing(pick);
                replacement.stackCount = Mathf.Clamp(t.stackCount, 1, pick.stackLimit);

                things[i] = replacement;
                swapped++;
            }
            return swapped;
        }
    }
}
