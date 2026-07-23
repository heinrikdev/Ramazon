using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Ramazon
{
    /// <summary>
    /// Entrega do Ramazon. Tudo passa pelo portal construido E abastecido com ouro:
    /// sem portal, ou com o portal apagado, nao ha entrega.
    /// </summary>
    public static class RamazonDrop
    {
        /// <summary>Cor do efeito de chegada (combina com o portal aceso).</summary>
        public static readonly Color MarkerColor = new Color(1f, 0.92f, 0.25f);

        public const string NoPortalReason = "Nenhum portal Ramazon no mapa.";
        public const string NoFuelReason = "O portal esta sem ouro - abasteca para reativar a rede.";

        /// <summary>Acha o portal construido no mapa (null se nao houver).</summary>
        public static Building_RamazonPortal FindPortal(Map map)
        {
            if (map == null) return null;
            var pdef = DefDatabase<ThingDef>.GetNamedSilentFail(Building_RamazonPortal.PortalDefName);
            if (pdef == null) return null;

            var list = map.listerThings.ThingsOfDef(pdef);
            for (int i = 0; i < list.Count; i++)
            {
                var b = list[i] as Building_RamazonPortal;
                if (b != null && b.Spawned) return b;
            }
            return null;
        }

        /// <summary>Existe portal (mesmo apagado)?</summary>
        public static bool HasPortal(Map map)
        {
            return FindPortal(map) != null;
        }

        /// <summary>Portal existe E esta queimando ouro?</summary>
        public static bool IsOnline(Map map)
        {
            var p = FindPortal(map);
            return p != null && p.IsActive;
        }

        /// <summary>Null se da pra operar; senao o motivo.</summary>
        public static string NotReadyReason(Map map)
        {
            var p = FindPortal(map);
            if (p == null) return NoPortalReason;
            if (!p.IsActive) return NoFuelReason;
            return null;
        }

        /// <summary>Celula onde as entregas saem, ou Invalid se nao ha portal.</summary>
        public static IntVec3 GetSpot(Map map)
        {
            var portal = FindPortal(map);
            return portal != null ? portal.DeliveryCell : IntVec3.Invalid;
        }

        /// <summary>
        /// Materializa as coisas na frente do portal.
        /// Retorna false se nao houver portal operante (nada e entregue).
        /// </summary>
        public static bool Deliver(Map map, List<Thing> things)
        {
            if (map == null || things == null || things.Count == 0) return false;

            var portal = FindPortal(map);
            if (portal == null || !portal.IsActive) return false;

            // Sem archolink a rede erra: parte vira outro produto, sempre mais barato.
            int swapped = RamazonAccuracy.Garble(map, things);
            if (swapped > 0)
            {
                Messages.Message(
                    swapped == 1
                        ? "O portal materializou 1 item errado (sem archolink, a rede erra)."
                        : $"O portal materializou {swapped} itens errados (sem archolink, a rede erra).",
                    MessageTypeDefOf.NegativeEvent, false);
            }

            var cell = portal.DeliveryCell;
            foreach (var t in things)
            {
                if (t == null) continue;

                var pawn = t as Pawn;
                if (pawn != null)
                {
                    // Pawns (animais) precisam de GenSpawn; TryPlaceThing nao serve bem.
                    GenSpawn.Spawn(pawn, cell, map, WipeMode.Vanish);
                }
                else
                {
                    // Near: empilha na celula e transborda pras vizinhas se nao couber.
                    GenPlace.TryPlaceThing(t, cell, map, ThingPlaceMode.Near);
                }
            }

            PortalFlash(map, cell);
            return true;
        }

        /// <summary>Efeito visual/sonoro de "saiu pelo portal".</summary>
        public static void PortalFlash(Map map, IntVec3 cell)
        {
            if (map == null || !cell.IsValid || !cell.InBounds(map)) return;

            var loc = cell.ToVector3Shifted();
            FleckMaker.ThrowLightningGlow(loc, map, 1.6f);
            for (int i = 0; i < 3; i++)
                FleckMaker.ThrowDustPuffThick(loc, map, 1.4f, MarkerColor);

            // Som buscado por nome (nao quebra se a DLC/versao nao tiver).
            var snd = DefDatabase<SoundDef>.GetNamedSilentFail("Psycast_Skip_Exit")
                   ?? DefDatabase<SoundDef>.GetNamedSilentFail("Psycast_Skip_Entry")
                   ?? SoundDefOf.ExecuteTrade;
            if (snd != null) snd.PlayOneShot(new TargetInfo(cell, map));
        }

        /// <summary>Texto de estado pra UI do carrinho.</summary>
        public static string StatusLabel(Map map)
        {
            var portal = FindPortal(map);
            if (portal == null) return "Sem portal no mapa";
            if (!portal.IsActive) return "Portal SEM OURO - rede offline";
            var c = portal.DeliveryCell;
            return "Portal online em " + c.x + ", " + c.z;
        }
    }
}
